using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace LolHighlight.Recording.Services;

public sealed record TelemetryArchiveRequest(string Path, DateTime Start, DateTime End);
public sealed record TelemetryArchiveResult(int Chunks, long Records, long Bytes);

/// <summary>Independent gzip NDJSON UUID boxes keep large match telemetry out of the bounded UI metadata.</summary>
public static class TelemetryArchive
{
    private static readonly byte[] ArchiveUuid = new Guid("842912ce-c331-43af-9928-ecabbcf7fa55").ToByteArray(bigEndian: true);
    private const int ChunkBytes = 1024 * 1024;
    private const int MaxChunkBytes = 2 * ChunkBytes;

    public static async Task<TelemetryArchiveResult> AppendAsync(string video, TelemetryArchiveRequest request,
        CancellationToken cancellationToken = default, Action<long>? reserveBytes = null)
    {
        if (!File.Exists(request.Path)) return new(0, 0, 0);
        await using var input = new FileStream(request.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true);
        // Fix the input range: the collector may still append records during a highlight export.
        var inputLength = input.Length;
        using var reader = new StreamReader(input);
        await using var output = new FileStream(video, FileMode.Append, FileAccess.Write, FileShare.Read, 65536, true);
        var originalLength = output.Length;
        using var chunk = new MemoryStream();
        var chunks = 0;
        long records = 0, bytes = 0, consumed = 0;
        async Task FlushChunk()
        {
            if (chunk.Length == 0) return;
            using var packed = new MemoryStream();
            await using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, true))
                await gzip.WriteAsync(chunk.GetBuffer().AsMemory(0, (int)chunk.Length), cancellationToken);
            var header = new byte[28];
            BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)(header.Length + packed.Length)));
            "uuid"u8.CopyTo(header.AsSpan(4));
            ArchiveUuid.CopyTo(header, 8);
            "LHT1"u8.CopyTo(header.AsSpan(24));
            reserveBytes?.Invoke(header.Length + packed.Length + 65536);
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(packed.GetBuffer().AsMemory(0, (int)packed.Length), cancellationToken);
            bytes += header.Length + packed.Length;
            chunks++;
            chunk.SetLength(0);
        }
        try
        {
            while (consumed < inputLength && await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                consumed += Encoding.UTF8.GetByteCount(line) + 1; // Collector always writes LF.
                if (!InWindow(line, request.Start, request.End)) continue;
                var data = Encoding.UTF8.GetBytes(line + "\n");
                if (data.Length > MaxChunkBytes) throw new IOException("单条遥测记录超过 2 MB，原始数据保留在会话目录中");
                if (chunk.Length + data.Length > MaxChunkBytes) await FlushChunk();
                chunk.Write(data);
                records++;
                if (chunk.Length >= ChunkBytes) await FlushChunk();
            }
            await FlushChunk();
            return new(chunks, records, bytes);
        }
        catch
        {
            // A failed archive must not leave an incomplete MP4 box after the playable video.
            try { output.SetLength(originalLength); } catch { }
            throw;
        }
    }

    public static async Task ExtractAsync(string video, Stream destination, CancellationToken cancellationToken = default)
    {
        await using var input = File.OpenRead(video);
        var header = new byte[8];
        while (input.Position < input.Length)
        {
            await input.ReadExactlyAsync(header, cancellationToken);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerSize = 8;
            if (size == 1)
            {
                await input.ReadExactlyAsync(header, cancellationToken);
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header));
                headerSize = 16;
                // Large boxes are video mdat; archive boxes are always 32-bit sized.
                if (size < headerSize || size - headerSize > input.Length - input.Position) throw new InvalidDataException("无效 MP4 数据块");
                input.Position += size - headerSize;
                continue;
            }
            if (size == 0) size = input.Length - input.Position + headerSize;
            if (size < headerSize || size - headerSize > input.Length - input.Position) throw new InvalidDataException("无效 MP4 数据块");
            var end = input.Position + size - headerSize;
            if (header.AsSpan(4).SequenceEqual("uuid"u8) && size >= 28 && size <= MaxChunkBytes + 65536)
            {
                var id = new byte[20];
                await input.ReadExactlyAsync(id, cancellationToken);
                if (id.AsSpan(0, 16).SequenceEqual(ArchiveUuid) && id.AsSpan(16).SequenceEqual("LHT1"u8))
                {
                    var packed = new byte[end - input.Position];
                    await input.ReadExactlyAsync(packed, cancellationToken);
                    using var memory = new MemoryStream(packed);
                    await using var gzip = new GZipStream(memory, CompressionMode.Decompress);
                    var buffer = new byte[65536];
                    var total = 0;
                    int count;
                    while ((count = await gzip.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        if ((total += count) > MaxChunkBytes) throw new InvalidDataException("遥测数据块解压后超出上限");
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    }
                }
            }
            input.Position = end;
        }
    }

    private static bool InWindow(string line, DateTime start, DateTime end)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.TryGetProperty("type", out var type) && type.GetString() == "session" ||
                root.TryGetProperty("captured_at", out var captured) &&
                DateTime.TryParse(captured.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) &&
                time >= start && time <= end;
        }
        catch (JsonException) { return false; }
    }
}
