namespace LolHighlight.Recording.Models;

public record MemorySegment
{
    public uint Index { get; init; }
    public double TimestampStart { get; init; }
    public double TimestampEnd { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
}
