namespace LolHighlight.Recording.Services;

/// <summary>Groups complete TS packets at video random-access boundaries, retaining program tables.</summary>
internal sealed class TransportStreamSegmenter : IDisposable
{
    private const int PacketSize = 188;
    // Explicitly assigned by the recording pipeline; video is mapped first.
    private const int VideoPid = 256;
    private const int PmtPid = 4096;
    private readonly Action<MemorySegment> _emit;
    private readonly double _targetSeconds;
    private readonly double _frameSeconds;
    private readonly long _maxSegmentBytes;
    private readonly byte[] _packet = new byte[PacketSize];
    private readonly MemoryStream _segment = new();
    private byte[]? _pat;
    private byte[]? _pmt;
    private int _packetBytes;
    private uint _index;
    private double? _firstPts;
    private double _firstArrival;
    private double _start;
    private double _lastFrame;
    private bool _started;

    internal TransportStreamSegmenter(Action<MemorySegment> emit, double targetSeconds, int fps, long maxSegmentBytes)
    {
        _emit = emit;
        _targetSeconds = targetSeconds;
        _frameSeconds = 1d / fps;
        _maxSegmentBytes = maxSegmentBytes;
    }

    internal void Add(ReadOnlySpan<byte> data, double arrivalSeconds)
    {
        while (!data.IsEmpty)
        {
            var count = Math.Min(PacketSize - _packetBytes, data.Length);
            data[..count].CopyTo(_packet.AsSpan(_packetBytes));
            _packetBytes += count;
            data = data[count..];
            if (_packetBytes != PacketSize) continue;
            if (_packet[0] != 0x47) throw new InvalidDataException("FFmpeg output is not a packet-aligned transport stream");
            ProcessPacket(arrivalSeconds);
            _packetBytes = 0;
        }
    }

    private void ProcessPacket(double arrivalSeconds)
    {
        var pid = ((_packet[1] & 0x1f) << 8) | _packet[2];
        if (pid == 0) _pat = _packet.ToArray();
        if (pid == PmtPid) _pmt = _packet.ToArray();
        var hasAdaptation = (_packet[3] & 0x20) != 0;
        var hasPayload = (_packet[3] & 0x10) != 0;
        var payloadStart = (_packet[1] & 0x40) != 0;
        var payload = 4 + (hasAdaptation ? 1 + _packet[4] : 0);
        if (pid == VideoPid && payloadStart && hasPayload && payload + 14 <= PacketSize &&
            _packet[payload] == 0 && _packet[payload + 1] == 0 && _packet[payload + 2] == 1 &&
            (_packet[payload + 7] & 0x80) != 0)
        {
            var pts = ReadPts(_packet.AsSpan(payload + 9, 5)) / 90000d;
            if (_firstPts == null)
            {
                _firstPts = pts;
                _firstArrival = arrivalSeconds;
            }
            var elapsed = pts - _firstPts.Value;
            if (elapsed < -1) elapsed += (1L << 33) / 90000d; // 33-bit PTS wrap.
            _lastFrame = Math.Max(_lastFrame, _firstArrival + elapsed);
            var randomAccess = hasAdaptation && _packet[4] > 0 && (_packet[5] & 0x40) != 0;
            if (randomAccess && _pat != null && _pmt != null)
            {
                if (_started && _lastFrame - _start >= _targetSeconds - _frameSeconds / 2)
                    Emit(_lastFrame);
                if (!_started || _segment.Length == 0)
                {
                    _start = _lastFrame;
                    _started = true;
                    _segment.Write(_pat);
                    _segment.Write(_pmt);
                }
            }
        }
        if (!_started) return;
        if (_segment.Length + PacketSize > _maxSegmentBytes)
            throw new InvalidDataException("单个视频 GOP 超过缓存预算，请降低码率或增大内存缓冲");
        _segment.Write(_packet);
    }

    private void Emit(double end)
    {
        if (_segment.Length == 0) return;
        _emit(new MemorySegment
        {
            Index = _index++,
            TimestampStart = _start,
            TimestampEnd = Math.Max(end, _start + _frameSeconds),
            Data = _segment.ToArray()
        });
        _segment.SetLength(0);
    }

    internal void Complete()
    {
        Emit(_lastFrame + _frameSeconds);
        if (_packetBytes != 0) throw new InvalidDataException("Truncated final MPEG-TS packet");
    }

    private static long ReadPts(ReadOnlySpan<byte> pts) =>
        ((long)(pts[0] & 0x0e) << 29) | ((long)pts[1] << 22) |
        ((long)(pts[2] & 0xfe) << 14) | ((long)pts[3] << 7) | ((long)pts[4] >> 1);

    public void Dispose() => _segment.Dispose();
}
