using System.Diagnostics;

namespace LolHighlight.Recording.Services;

public interface IRecordingClock
{
    bool IsRunning { get; }
    DateTime StartedAtUtc { get; }
    double NowSeconds { get; }
    void Start();
    void Stop();
    double ToSessionSeconds(DateTime utcTime);
}

public sealed class RecordingClock : IRecordingClock
{
    private readonly object _lock = new();
    private long _startedTimestamp;
    private DateTime _startedAtUtc;
    private bool _isRunning;

    public bool IsRunning
    {
        get { lock (_lock) return _isRunning; }
    }

    public DateTime StartedAtUtc
    {
        get { lock (_lock) return _startedAtUtc; }
    }

    public double NowSeconds
    {
        get
        {
            lock (_lock)
            {
                return _isRunning ? ElapsedSeconds(_startedTimestamp) : 0;
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            _startedTimestamp = Stopwatch.GetTimestamp();
            _startedAtUtc = DateTime.UtcNow;
            _isRunning = true;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isRunning = false;
        }
    }

    public double ToSessionSeconds(DateTime utcTime)
    {
        lock (_lock)
        {
            if (!_isRunning)
                return 0;

            var seconds = (utcTime.ToUniversalTime() - _startedAtUtc).TotalSeconds;
            return Math.Max(0, seconds);
        }
    }

    private static double ElapsedSeconds(long startedTimestamp)
    {
        return (Stopwatch.GetTimestamp() - startedTimestamp) / (double)Stopwatch.Frequency;
    }
}
