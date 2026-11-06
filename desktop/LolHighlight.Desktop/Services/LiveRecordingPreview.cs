using System.Diagnostics;

namespace LolHighlight.Desktop.Services;

/// <summary>Decodes the existing recording stream to a silent, in-memory preview.</summary>
internal sealed class LiveRecordingPreview
{
    private readonly CancellationTokenSource _stop = new();
    private readonly RecordingPreviewTap.Subscription _subscription;
    private readonly Task _worker;

    public LiveRecordingPreview(RecordingPreviewTap tap, AppConfig config, Action<byte[]> frame, Action<string> failed)
    {
        _subscription = tap.Subscribe();
        _worker = Task.Run(() => RunAsync(config, frame, failed));
    }

    private async Task RunAsync(AppConfig config, Action<byte[]> frame, Action<string> failed)
    {
        var token = _stop.Token;
        try
        {
            var start = new ProcessStartInfo(FfmpegExecutable.Resolve(config.FfmpegPath))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "2",
                "-probesize", "1048576", "-analyzeduration", "1000000", "-fflags", "+discardcorrupt", "-f", "mpegts", "-i", "pipe:0",
                "-map", "0:v:0", "-an", "-sn", "-dn", "-vf", "fps=10,scale=960:540:force_original_aspect_ratio=decrease",
                "-threads", "2", "-c:v", "mjpeg", "-q:v", "5", "-fps_mode", "passthrough", "-flush_packets", "1", "-f", "image2pipe", "pipe:1" }) start.ArgumentList.Add(argument);
            token.ThrowIfCancellationRequested();
            using var process = Process.Start(start) ?? throw new IOException("无法启动录制预览。");
            using var cancelProcess = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception) { } });
            var errors = ProcessRunner.ReadBoundedTextAsync(process.StandardError);
            var input = WriteInputAsync(process.StandardInput.BaseStream, token);
            var output = ReadFramesAsync(process.StandardOutput.BaseStream, frame, token);
            try
            {
                await Task.WhenAny(input, output);
                _subscription.Dispose();
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                try { await Task.WhenAll(input, output); }
                catch (IOException) when (token.IsCancellationRequested) { }
                var error = await errors;
                if (!token.IsCancellationRequested) failed(string.IsNullOrWhiteSpace(error) ? "录制预览已断开" : "录制预览暂不可用");
            }
            finally
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
                // Complete the reader as well when the decoder exits unexpectedly.
                _subscription.Dispose();
                try { await Task.WhenAll(input, output, errors); } catch (Exception) { }
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        { if (!token.IsCancellationRequested) failed("录制预览暂不可用"); }
        finally { _subscription.Dispose(); }
    }

    private async Task WriteInputAsync(Stream input, CancellationToken token)
    {
        await foreach (var data in _subscription.Reader.ReadAllAsync(token))
            await input.WriteAsync(data, token);
    }

    private static async Task ReadFramesAsync(Stream output, Action<byte[]> frame, CancellationToken token)
    {
        var buffer = new byte[16384];
        using var jpeg = new MemoryStream();
        var previous = -1;
        var collecting = false;
        int count;
        while ((count = await output.ReadAsync(buffer, token)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var value = buffer[i];
                if (!collecting && previous == 0xFF && value == 0xD8)
                { jpeg.SetLength(0); jpeg.Position = 0; jpeg.WriteByte(0xFF); collecting = true; }
                if (collecting)
                {
                    jpeg.WriteByte(value);
                    if (previous == 0xFF && value == 0xD9)
                    { frame(jpeg.ToArray()); collecting = false; }
                    else if (jpeg.Length > 4 * 1024 * 1024) { collecting = false; jpeg.SetLength(0); }
                }
                previous = value;
            }
        }
    }

    public async Task StopAsync()
    {
        _stop.Cancel(); _subscription.Dispose();
        try { await _worker; }
        finally { _stop.Dispose(); }
    }
}
