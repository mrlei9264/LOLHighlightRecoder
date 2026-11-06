using System.Threading.Channels;

namespace LolHighlight.Recording.Services;

/// <summary>A bounded, optional copy of the encoded recording stream. Never waits on the recorder.</summary>
public sealed class RecordingPreviewTap
{
    private Subscription? _subscriber;

    public Subscription Subscribe()
    {
        var next = new Subscription(this);
        Interlocked.Exchange(ref _subscriber, next)?.Dispose();
        return next;
    }

    public void Publish(ReadOnlyMemory<byte> data)
    {
        var subscriber = Volatile.Read(ref _subscriber);
        if (subscriber != null) subscriber.Queue.Writer.TryWrite(data.ToArray());
    }

    public sealed class Subscription : IDisposable
    {
        private readonly RecordingPreviewTap _owner;
        internal readonly Channel<byte[]> Queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(48)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        internal Subscription(RecordingPreviewTap owner) => _owner = owner;
        public ChannelReader<byte[]> Reader => Queue.Reader;
        public void Dispose()
        {
            Interlocked.CompareExchange(ref _owner._subscriber, null, this);
            Queue.Writer.TryComplete();
        }
    }
}
