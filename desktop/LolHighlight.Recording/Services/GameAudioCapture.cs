using NAudio.Wave;

namespace LolHighlight.Recording.Services;

public interface IGameAudioCapture : IDisposable
{
    Task StartAsync(int processId, Action<byte[]> onData);
    void Stop();
    bool IsRunning { get; }
    string Description { get; }
}

/// <summary>Captures only the selected game process and its child processes.</summary>
public sealed class GameAudioCapture : IGameAudioCapture
{
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    public bool IsRunning => _captureTask is { IsCompleted: false };
    public string Description => "process_loopback";

    public async Task StartAsync(int processId, Action<byte[]> onData)
    {
        Stop();
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("此 Windows 版本不支持游戏进程音频隔离，将仅录制画面。");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _captureTask = Task.Run(async () =>
        {
            try
            {
                var activation = ProcessAudioActivation.ActivateAsync(processId);
                NAudio.CoreAudioApi.AudioClient client;
                try { client = await activation.WaitAsync(TimeSpan.FromSeconds(5), token); }
                catch
                {
                    _ = DisposeLateActivationAsync(activation);
                    throw;
                }
                using var clientLifetime = client;
                client.Initialize(NAudio.CoreAudioApi.AudioClientShareMode.Shared,
                    NAudio.CoreAudioApi.AudioClientStreamFlags.Loopback |
                    NAudio.CoreAudioApi.AudioClientStreamFlags.AutoConvertPcm,
                    0, 0, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), Guid.Empty);
                var capture = client.AudioCaptureClient;
                client.Start();
                ready.TrySetResult();
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        while (capture.GetNextPacketSize() > 0)
                        {
                            var pointer = capture.GetBuffer(out var frames, out var flags);
                            try
                            {
                                var data = new byte[frames * 8];
                                if ((flags & NAudio.CoreAudioApi.AudioClientBufferFlags.Silent) == 0)
                                    System.Runtime.InteropServices.Marshal.Copy(pointer, data, 0, data.Length);
                                onData(data);
                            }
                            finally { capture.ReleaseBuffer(frames); }
                        }
                        await Task.Delay(10, token);
                    }
                }
                finally { client.Stop(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { ready.TrySetCanceled(token); }
            catch (Exception error) { ready.TrySetException(error); throw; }
        }, token);
        try { await ready.Task; }
        catch { Stop(); throw; }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _captureTask?.GetAwaiter().GetResult(); } catch { }
        _captureTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private static async Task DisposeLateActivationAsync(Task<NAudio.CoreAudioApi.AudioClient> activation)
    {
        try { (await activation).Dispose(); }
        catch (Exception) { /* Observe activation failure after the capture has already stopped. */ }
    }
}
