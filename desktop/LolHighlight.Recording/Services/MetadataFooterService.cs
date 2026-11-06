using LolHighlight.Recording.Infrastructure;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LolHighlight.Recording.Services;

public interface IMetadataFooterService
{
    long AppendMetadataFooter(string path, Dictionary<string, object> metadata);
    Dictionary<string, object>? ReadMetadataFooter(string path);
}

public sealed class MetadataFooterService : IMetadataFooterService
{
    private static readonly Guid MetadataUuid = new("5e9ef2f5-5cb2-4e4f-a719-4c4f4c484c52");
    private static readonly byte[] BoxUuid = MetadataUuid.ToByteArray(bigEndian: true);
    private static readonly byte[] LegacyBoxUuid = MetadataUuid.ToByteArray();
    private static readonly byte[] BoxType = "uuid"u8.ToArray();
    private static readonly byte[] PayloadMagic = "LHRM"u8.ToArray();
    private const int PayloadVersion = 1;
    private const int CodecDeflate = 1;
    private const int HeaderSize = 4 + 1 + 1 + 8 + 32;
    internal const int MaxMetadataBytes = 16 * 1024 * 1024;

    public long AppendMetadataFooter(string path, Dictionary<string, object> metadata)
    {
        var rawBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata));
        if (rawBytes.Length > MaxMetadataBytes)
            throw new InvalidOperationException("Metadata exceeds the 16 MB limit");
        using var compressedStream = new MemoryStream();
        using (var deflateStream = new DeflateStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflateStream.Write(rawBytes, 0, rawBytes.Length);
        }

        var compressedBytes = compressedStream.ToArray();
        var hash = SHA256.HashData(rawBytes);
        var header = new byte[HeaderSize];
        Buffer.BlockCopy(PayloadMagic, 0, header, 0, 4);
        header[4] = PayloadVersion;
        header[5] = CodecDeflate;
        WriteInt64BigEndian(header.AsSpan(6, 8), rawBytes.Length);
        Buffer.BlockCopy(hash, 0, header, 14, hash.Length);

        var payloadSize = BoxUuid.Length + header.Length + compressedBytes.Length;
        var boxSize = 8L + payloadSize;
        if (boxSize > uint.MaxValue)
            throw new InvalidOperationException("Metadata footer is too large for a 32-bit MP4 box");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var target = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var sizeBytes = new byte[4];
        WriteUInt32BigEndian(sizeBytes, (uint)boxSize);
        target.Write(sizeBytes, 0, sizeBytes.Length);
        target.Write(BoxType, 0, BoxType.Length);
        target.Write(BoxUuid, 0, BoxUuid.Length);
        target.Write(header, 0, header.Length);
        target.Write(compressedBytes, 0, compressedBytes.Length);
        target.Flush(true);

        return boxSize;
    }

    public Dictionary<string, object>? ReadMetadataFooter(string path)
    {
        if (!File.Exists(path))
            return null;

        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Dictionary<string, object>? latest = null;

        while (source.Position < source.Length)
        {
            var boxStart = source.Position;
            var header = ReadBoxHeader(source);
            if (header == null)
                break;

            var (size, boxType, headerSize) = header.Value;
            if (size > source.Length - boxStart)
                throw new InvalidOperationException("MP4 box extends beyond file end");

            var contentSize = size - headerSize;
            if (boxType.SequenceEqual(BoxType) && contentSize >= BoxUuid.Length + HeaderSize)
            {
                var uuid = new byte[BoxUuid.Length];
                source.ReadExactly(uuid);
                if (!uuid.AsSpan().SequenceEqual(BoxUuid) && !uuid.AsSpan().SequenceEqual(LegacyBoxUuid))
                {
                    source.Position = boxStart + size;
                    continue;
                }
                if (contentSize > MaxMetadataBytes + HeaderSize + BoxUuid.Length + 1024)
                    throw new InvalidOperationException("Metadata footer box is too large to read");
                var boxContent = new byte[contentSize];
                uuid.CopyTo(boxContent, 0);
                source.ReadExactly(boxContent.AsSpan(BoxUuid.Length));
                var payloadUuid = boxContent.AsSpan(0, BoxUuid.Length);
                if (payloadUuid.SequenceEqual(BoxUuid) || payloadUuid.SequenceEqual(LegacyBoxUuid))
                {
                    var packed = boxContent.AsSpan(BoxUuid.Length, HeaderSize);
                    if (packed[..4].SequenceEqual(PayloadMagic) && packed[4] == PayloadVersion)
                    {
                        var codec = packed[5];
                        var rawSize = ReadInt64BigEndian(packed.Slice(6, 8));
                        var expectedHash = packed.Slice(14, 32).ToArray();
                        var encoded = boxContent.AsSpan(BoxUuid.Length + HeaderSize).ToArray();

                        if (codec != CodecDeflate)
                            throw new InvalidOperationException("Unsupported metadata compression codec");
                        if (rawSize < 0 || rawSize > MaxMetadataBytes)
                            throw new InvalidOperationException("Invalid metadata size");

                        using var compressedStream = new MemoryStream(encoded);
                        using var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
                        // Bound expansion before allocating, including forged compressed payloads.
                        var rawBytes = new byte[(int)rawSize];
                        deflateStream.ReadExactly(rawBytes);
                        if (deflateStream.ReadByte() != -1)
                            throw new InvalidOperationException("Metadata expands beyond its declared size");

                        if (rawBytes.LongLength != rawSize || !SHA256.HashData(rawBytes).SequenceEqual(expectedHash))
                            throw new InvalidOperationException("Highlight metadata checksum failed");

                        var root = JsonSerializer.Deserialize<JsonElement>(rawBytes);
                        latest = JsonValues.Normalize(root) as Dictionary<string, object>;
                    }
                }
            }
            else
            {
                source.Position = boxStart + size;
            }
        }

        return latest;
    }

    private static (long Size, byte[] BoxType, int HeaderSize)? ReadBoxHeader(FileStream source)
    {
        if (source.Length - source.Position < 8)
            throw new InvalidOperationException("Truncated MP4 box header");

        var prefix = new byte[8];
        source.ReadExactly(prefix);
        var size32 = ReadUInt32BigEndian(prefix.AsSpan(0, 4));
        var boxType = prefix[4..8];

        long size;
        var headerSize = 8;
        if (size32 == 1)
        {
            var extended = new byte[8];
            source.ReadExactly(extended);
            size = ReadInt64BigEndian(extended);
            headerSize = 16;
        }
        else if (size32 == 0)
        {
            size = source.Length - (source.Position - 8);
        }
        else
        {
            size = size32;
        }

        if (size < headerSize)
            throw new InvalidOperationException("Invalid MP4 box size");
        return (size, boxType, headerSize);
    }

    private static void WriteUInt32BigEndian(Span<byte> target, uint value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> source)
    {
        return (uint)(source[0] << 24 | source[1] << 16 | source[2] << 8 | source[3]);
    }

    private static void WriteInt64BigEndian(Span<byte> target, long value)
    {
        for (var i = 7; i >= 0; i--)
            target[7 - i] = (byte)(value >> (i * 8));
    }

    private static long ReadInt64BigEndian(ReadOnlySpan<byte> source)
    {
        long value = 0;
        for (var i = 0; i < 8; i++)
            value = (value << 8) | source[i];
        return value;
    }

}
