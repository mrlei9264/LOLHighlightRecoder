using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace LolHighlight.Desktop.Services;

/// <summary>Extract actual video frames instead of Windows' file-type icon thumbnails.</summary>
public sealed class VideoThumbnailService(AppConfig config, AppPaths paths)
{
    private readonly SemaphoreSlim _workers = new(2, 2);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _pending = new();
    public async Task<string?> GetAsync(string video)
    {
        try
        {
            var file = new FileInfo(video);
            if (!file.Exists) return null;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")));
            var output = Path.Combine(paths.ResolveRuntimeDirectory(config.RuntimeDir), "thumbnails", key + ".jpg");
            if (File.Exists(output)) return output;
            var task = _pending.GetOrAdd(key, _ => new(() => ExtractAsync(file.FullName, output)));
            try { return await task.Value; }
            finally { _pending.TryRemove(key, out _); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private async Task<string?> ExtractAsync(string video, string output)
    {
        await _workers.WaitAsync();
        var partial = output + ".part.jpg";
        try
        {
            if (File.Exists(output)) return output;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            foreach (var seek in new[] { "0.5", "0" })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var start = new ProcessStartInfo(FfmpegExecutable.Resolve(config.FfmpegPath))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-v", "error", "-nostdin", "-y", "-ss", seek, "-i", video, "-frames:v", "1", "-an", "-vf",
                    "scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2", "-q:v", "3", partial }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("无法读取视频缩略图。");
                var errors = ProcessRunner.ReadBoundedTextAsync(process.StandardError);
                try { await process.WaitForExitAsync(timeout.Token); }
                finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await errors; }
                if (process.ExitCode == 0 && File.Exists(partial) && new FileInfo(partial).Length > 0)
                { File.Move(partial, output, true); return output; }
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception) { return null; }
        finally { try { File.Delete(partial); } catch (IOException) { } _workers.Release(); }
    }
}
