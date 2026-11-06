using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public sealed class MemoryRingBufferTests
{
    [Fact]
    public void PushSegment_EvictsOldestDataWhenMemoryLimitIsReached()
    {
        var ring = new MemoryRingBuffer(maxSeconds: 60, maxMemoryBytes: 10);

        ring.PushSegment(0, 0, 1, new byte[8]);
        ring.PushSegment(1, 1, 2, new byte[8]);

        Assert.Equal(1, ring.SegmentCount);
        Assert.Equal(8, ring.MemoryUsage);
        Assert.Equal(1u, ring.GetRange(0, 2).Single().Index);
    }

    [Fact]
    public void PushSegment_EvictsOldestDataWhenTimeWindowIsExceeded()
    {
        var ring = new MemoryRingBuffer(maxSeconds: 3, maxMemoryBytes: 1024);

        ring.PushSegment(0, 0, 2, new byte[1]);
        ring.PushSegment(1, 2, 4, new byte[1]);
        ring.PushSegment(2, 4, 6, new byte[1]);

        Assert.Equal(1, ring.SegmentCount);
        Assert.Equal(4d, ring.OldestTimestamp);
        Assert.Equal(6d, ring.LatestTimestamp);
    }

    [Fact]
    public void GetRange_ReturnsOnlyOverlappingSegmentsInInsertionOrder()
    {
        var ring = new MemoryRingBuffer(maxSeconds: 60, maxMemoryBytes: 1024);
        ring.PushSegment(0, 0, 1, new byte[1]);
        ring.PushSegment(1, 1, 2, new byte[1]);
        ring.PushSegment(2, 2, 3, new byte[1]);

        var result = ring.GetRange(1.5, 2.5);

        Assert.Equal(new uint[] { 1, 2 }, result.Select(segment => segment.Index));
    }
}
