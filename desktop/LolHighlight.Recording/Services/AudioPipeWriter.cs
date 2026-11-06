using System.IO.Pipes;
using System.Threading.Channels;

namespace LolHighlight.Recording.Services;

/// <summary>A single ordered writer with a bounded queue; capture callbacks never block on FFmpeg.</summary>
internal sealed class AudioPipeWriter
{
    private readonly NamedPipeServerStream _pipe;
    private readonly Action<string> _reportError;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    internal Task Completion { get; }

    internal AudioPipeWriter(NamedPipeServerStream pipe, Action<string> reportError, CancellationToken cancellationToken)
    {
        _pipe = pipe;
        _reportError = reportError;
        Completion = PumpAsync(cancellationToken);
    }

    internal void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length > 1024 * 1024) return;
        if (!_queue.Writer.TryWrite(data.ToArray()))
            _reportError("音频写入队列已满，请降低录制负载");
    }

    internal void Complete() => _queue.Writer.TryComplete();

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _pipe.WaitForConnectionAsync(cancellationToken);
            await foreach (var data in _queue.Reader.ReadAllAsync(cancellationToken))
                await FfmpegPipeline.WritePipeAsync(_pipe, data, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            _reportError($"音频管道中断：{error.Message}");
        }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) { }
            _pipe.Dispose();
        }
    }
}
