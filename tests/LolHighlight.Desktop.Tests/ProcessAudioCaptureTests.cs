using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public sealed class ProcessAudioCaptureTests
{
    [Fact]
    public async Task ProcessLoopback_CapturesOnlySilentTestProcessAndStops()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) return;
        using var capture = new GameAudioCapture();
        var packet = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await capture.StartAsync(Environment.ProcessId, data => packet.TrySetResult(data))
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(capture.IsRunning);
        var pcm = await packet.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(pcm);
        Assert.All(pcm, value => Assert.Equal(0, value));
        capture.Stop();
        Assert.False(capture.IsRunning);
    }
}
