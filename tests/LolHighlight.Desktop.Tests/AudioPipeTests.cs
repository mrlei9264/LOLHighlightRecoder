using System.IO.Pipes;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public sealed class AudioPipeTests
{
    [Fact]
    public async Task QueuedAudio_PreservesAllPacketOrderingAndStopsCleanly()
    {
        var name = $"rift_test_{Guid.NewGuid():N}";
        using var server = FfmpegPipeline.CreatePipe(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var errors = new List<string>();
        var writer = new AudioPipeWriter(server, errors.Add, cancellation.Token);
        for (byte i = 0; i < 32; i++) writer.Write(new byte[] { i });
        await client.ConnectAsync(cancellation.Token);
        var actual = new byte[32];
        await client.ReadExactlyAsync(actual, cancellation.Token);
        Assert.Equal(Enumerable.Range(0, 32).Select(value => (byte)value), actual);
        cancellation.Cancel();
        await writer.Completion;
        Assert.Empty(errors);
    }

    [Fact]
    public async Task AudioPipe_CanConnectAndWritePcm()
    {
        var name = $"rift_test_{Guid.NewGuid():N}";
        using var server = FfmpegPipeline.CreatePipe(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await connection;

        var expected = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await FfmpegPipeline.WritePipeAsync(server, expected);
        var actual = new byte[expected.Length];
        await client.ReadExactlyAsync(actual, timeout.Token);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task AudioPipe_WhenReaderStalls_WriteIsCancelled()
    {
        var name = $"rift_test_{Guid.NewGuid():N}";
        using var server = FfmpegPipeline.CreatePipe(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await connection;

        // Exceed the pipe buffer without reading: shutdown must remain possible.
        var write = FfmpegPipeline.WritePipeAsync(server, new byte[8 * 1024 * 1024]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await write.WaitAsync(timeout.Token));
        Assert.True(write.IsCompleted);
    }
}
