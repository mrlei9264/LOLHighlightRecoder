using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public sealed class TransportStreamTests
{
    [FfmpegFact]
    public async Task SegmentingAcrossArbitraryReadBoundaries_ExportsDecodableMiddleWithTwoAudioTracks()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Path, "source.ts");
        await FfmpegTestTools.RunAsync("-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000",
            "-f", "lavfi", "-i", "sine=frequency=900:sample_rate=48000",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-t", "8",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "20", "-bf", "0", "-sc_threshold", "0",
            "-c:a", "aac", "-mpegts_start_pid", "256", "-mpegts_pmt_start_pid", "4096", "-f", "mpegts", source);
        var segments = new List<MemorySegment>();
        using (var segmenter = new TransportStreamSegmenter(segments.Add, 1, 10, 16 * 1024 * 1024))
        {
            var data = await File.ReadAllBytesAsync(source);
            for (var offset = 0; offset < data.Length; offset += 137)
                segmenter.Add(data.AsSpan(offset, Math.Min(137, data.Length - offset)), 1);
            segmenter.Complete();
        }
        Assert.Equal(4, segments.Count);
        Assert.All(segments, segment => Assert.Equal(0, segment.Data.Length % 188));
        var ring = new MemoryRingBuffer(60, 32 * 1024 * 1024);
        foreach (var segment in segments)
            ring.PushSegment(segment.Index, segment.TimestampStart, segment.TimestampEnd, segment.Data);
        var config = new AppConfig { FfmpegPath = FfmpegTestTools.FindExecutable()!, MinimumFreeGb = 1 };
        var exporter = new ExportManager(ring, new MetadataFooterService(), new StorageQuotaManager(), config);
        var output = await exporter.ExportAsync(4.2, 6.5, Path.Combine(directory.Path, "middle.mp4"), new());
        // Decode video and both preserved audio tracks. A nonempty MP4 alone cannot detect missing keyframes.
        await FfmpegTestTools.RunAsync("-hide_banner", "-loglevel", "error", "-xerror", "-i", output,
            "-map", "0:v:0", "-map", "0:a:0", "-map", "0:a:1", "-f", "null", "-");
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part.mp4"));
    }

    [Fact]
    public void Segmenter_RejectsMalformedTransportPackets()
    {
        using var segmenter = new TransportStreamSegmenter(_ => { }, 1, 60, 1024);
        Assert.Throws<InvalidDataException>(() => segmenter.Add(new byte[188], 0));
    }
}
