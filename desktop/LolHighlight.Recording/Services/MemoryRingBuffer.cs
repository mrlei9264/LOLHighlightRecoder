namespace LolHighlight.Recording.Services;

public interface IMemoryRingBuffer
{
    void PushSegment(uint index, double startTime, double endTime, byte[] data);
    List<MemorySegment> GetRange(double startTime, double endTime);
    double LatestTimestamp { get; }
    double OldestTimestamp { get; }
    double BufferedSeconds { get; }
    long MemoryUsage { get; }
    int SegmentCount { get; }
    void UpdateLimits(int maxSeconds, long maxMemoryBytes);
    void Clear();
}

public sealed class MemoryRingBuffer : IMemoryRingBuffer
{
    private readonly LinkedList<MemorySegment> _segments = new();
    private readonly object _lock = new();
    private int _maxSeconds;
    private long _maxMemoryBytes;
    private long _currentMemoryBytes;

    public double LatestTimestamp
    {
        get
        {
            lock (_lock)
            {
                return _segments.Count > 0 ? _segments.Last!.Value.TimestampEnd : 0;
            }
        }
    }

    public double OldestTimestamp
    {
        get
        {
            lock (_lock)
            {
                return _segments.Count > 0 ? _segments.First!.Value.TimestampStart : 0;
            }
        }
    }

    public double BufferedSeconds
    {
        get
        {
            lock (_lock)
            {
                if (_segments.Count == 0) return 0;
                return _segments.Last!.Value.TimestampEnd - _segments.First!.Value.TimestampStart;
            }
        }
    }

    public long MemoryUsage
    {
        get
        {
            lock (_lock)
            {
                return _currentMemoryBytes;
            }
        }
    }

    public int SegmentCount
    {
        get
        {
            lock (_lock)
            {
                return _segments.Count;
            }
        }
    }

    public MemoryRingBuffer(int maxSeconds, long maxMemoryBytes)
    {
        _maxSeconds = Math.Max(1, maxSeconds);
        _maxMemoryBytes = Math.Max(1, maxMemoryBytes);
    }

    public void PushSegment(uint index, double startTime, double endTime, byte[] data)
    {
        if (data.Length == 0 || !double.IsFinite(startTime) || !double.IsFinite(endTime) ||
            startTime < 0 || endTime <= startTime)
            return;

        lock (_lock)
        {
            if (_segments.Last is { Value: var previous } &&
                (index <= previous.Index || startTime < previous.TimestampStart))
                throw new ArgumentException("Memory segments must be ordered by index and time");
            var segment = new MemorySegment
            {
                Index = index,
                TimestampStart = startTime,
                TimestampEnd = endTime,
                Data = data
            };

            _segments.AddLast(segment);
            _currentMemoryBytes += data.Length;

            EvictOldSegments();
        }
    }

    public List<MemorySegment> GetRange(double startTime, double endTime)
    {
        if (!double.IsFinite(startTime) || !double.IsFinite(endTime) || endTime <= startTime)
            throw new ArgumentException("Invalid buffer range");
        lock (_lock)
        {
            var result = new List<MemorySegment>();
            foreach (var seg in _segments)
            {
                if (seg.TimestampEnd > startTime && seg.TimestampStart < endTime)
                {
                    result.Add(seg);
                }
            }
            return result;
        }
    }

    public void UpdateLimits(int maxSeconds, long maxMemoryBytes)
    {
        lock (_lock)
        {
            _maxSeconds = Math.Max(1, maxSeconds);
            _maxMemoryBytes = Math.Max(1, maxMemoryBytes);
            EvictOldSegments();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _segments.Clear();
            _currentMemoryBytes = 0;
        }
    }

    private void EvictOldSegments()
    {
        while (_segments.Count > 0 && _currentMemoryBytes > _maxMemoryBytes)
        {
            var first = _segments.First!;
            _currentMemoryBytes -= first.Value.Data.Length;
            _segments.RemoveFirst();
        }

        while (_segments.Count > 0)
        {
            var buffered = _segments.Last!.Value.TimestampEnd - _segments.First!.Value.TimestampStart;
            if (buffered <= _maxSeconds) break;

            var first = _segments.First!;
            _currentMemoryBytes -= first.Value.Data.Length;
            _segments.RemoveFirst();
        }
    }
}
