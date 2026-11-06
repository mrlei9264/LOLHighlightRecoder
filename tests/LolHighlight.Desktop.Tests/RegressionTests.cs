using System.Buffers.Binary;
using System.Text.Json;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Services;
using NAudio.Wave;

namespace LolHighlight.Desktop.Tests;

public sealed class RegressionTests
{
    [Theory]
    [InlineData("Player#TAG", true)]
    [InlineData("Player#OTHER", false)]
    [InlineData("Player", true)]
    [InlineData("Enemy#TAG", false)]
    public void PlayerMatching_DistinguishesIdenticalNamesWithDifferentTags(string name, bool matches)
        => Assert.Equal(matches, new PlayerIdentity("Player#TAG", "Player", "TAG").Matches(name));

    [Fact]
    public void StereoConversion_ReadsRightChannelWithinSingleFrame()
    {
        var pcm = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(pcm, 16384);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2), -16384);
        var result = AudioPcmConverter.ToFloat32Stereo(pcm, pcm.Length, new WaveFormat(48000, 16, 2));
        Assert.Equal(0.5f, BitConverter.ToSingle(result));
        Assert.Equal(-0.5f, BitConverter.ToSingle(result, 4));
    }

    [Fact]
    public void Resampling_ManySmallCallbacks_DoesNotAccumulateRoundingDrift()
    {
        var converter = new AudioPcmConverter();
        var format = new WaveFormat(44100, 16, 2);
        var samples = new byte[137 * 4];
        var frames = 0;
        for (var i = 0; i < 1000; i++) frames += converter.Convert(samples, samples.Length, format).Length / 8;
        var expected = 137_000 * 48000d / 44100;
        Assert.InRange(Math.Abs(expected - frames), 0, 2);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Configuration_RejectsNonFiniteDurations(double seconds)
        => Assert.Throws<ArgumentException>(() => new AppConfig { PreRollSeconds = seconds }.Validate());

    [Theory]
    [InlineData("0M")]
    [InlineData("0")]
    [InlineData("-1M")]
    public void Configuration_RejectsZeroOrNegativeBitrate(string bitrate)
        => Assert.Throws<ArgumentException>(() => new AppConfig { Bitrate = bitrate }.Validate());

    [Fact]
    public void Configuration_RejectsEmptyClip()
        => Assert.Throws<ArgumentException>(() => new AppConfig { PreRollSeconds = 0, PostRollSeconds = 0 }.Validate());

    [Fact]
    public void Buffer_SnapshotSurvivesEvictionWithoutCopyingVideo()
    {
        var ring = new MemoryRingBuffer(60, 8);
        var data = new byte[8];
        ring.PushSegment(0, 0, 1, data);
        var snapshot = ring.GetRange(0, 1);
        ring.PushSegment(1, 1, 2, new byte[8]);
        Assert.Equal(8, ring.MemoryUsage);
        Assert.Equal(1u, ring.GetRange(1, 2).Single().Index);
        Assert.Same(data, snapshot.Single().Data);
    }

    [Fact]
    public void Buffer_RejectsOutOfOrderSegments()
    {
        var ring = new MemoryRingBuffer(60, 1024);
        ring.PushSegment(3, 3, 4, new byte[8]);
        Assert.Throws<ArgumentException>(() => ring.PushSegment(2, 2, 3, new byte[8]));
    }

    [Fact]
    public void Quota_CountsProtectedOutputAndIncomingBytes()
    {
        using var directory = new TestDirectory();
        var output = Path.Combine(directory.Path, "highlight_current.mp4");
        var old = Path.Combine(directory.Path, "highlight_old.mp4");
        File.WriteAllBytes(output, new byte[2048]);
        File.WriteAllBytes(old, new byte[16]);
        new StorageQuotaManager().Enforce(directory.Path, 1, 0, output, 1024L * 1024 * 1024 - 1024);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void Metadata_RejectsExpansionBeyondDeclaredSize()
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "footer.mp4");
        var footer = new MetadataFooterService();
        footer.AppendMetadataFooter(file, new() { ["value"] = new string('x', 100_000) });
        using (var stream = File.OpenWrite(file))
        {
            stream.Position = 8 + 16 + 6;
            var forgedSize = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(forgedSize, 1);
            stream.Write(forgedSize);
        }
        Assert.Throws<InvalidOperationException>(() => footer.ReadMetadataFooter(file));
    }

    [Fact]
    public void Quota_DoesNotDeleteImportedVideos()
    {
        using var directory = new TestDirectory();
        var imported = Path.Combine(directory.Path, "family.mp4");
        var highlight = Path.Combine(directory.Path, "highlight_old.mp4");
        File.WriteAllBytes(imported, new byte[16]);
        File.WriteAllBytes(highlight, new byte[16]);
        new StorageQuotaManager().Enforce(directory.Path, 0, 0);
        Assert.True(File.Exists(imported));
        Assert.False(File.Exists(highlight));
    }

    [Fact]
    public void Metadata_WritesCanonicalUuidAndStillReadsLegacyOrder()
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "footer.mp4");
        var footer = new MetadataFooterService();
        footer.AppendMetadataFooter(file, new() { ["value"] = "legacy" });
        var uuid = new Guid("5e9ef2f5-5cb2-4e4f-a719-4c4f4c484c52");
        Assert.Equal(uuid.ToByteArray(bigEndian: true), File.ReadAllBytes(file)[8..24]);
        using (var stream = File.OpenWrite(file))
        {
            stream.Position = 8;
            stream.Write(uuid.ToByteArray());
        }
        Assert.Equal("legacy", footer.ReadMetadataFooter(file)!["value"]);
    }

    [Fact]
    public void Settings_SaveWhileListening_DoesNotChangeMemoryOrDisk()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        var config = new AppConfig();
        var settings = new SettingsService(path, config, () => true);
        Assert.Throws<InvalidOperationException>(() => settings.Save(new AppConfig { Fps = 30 }));
        Assert.Equal(60, config.Fps);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TelemetryReader_NormalizesNestedValuesAndFiltersTime()
    {
        using var directory = new TestDirectory();
        var now = DateTime.UtcNow;
        var path = Path.Combine(directory.Path, "telemetry.ndjson");
        File.WriteAllLines(path, new[]
        {
            JsonSerializer.Serialize(new { type = "session", player = new { name = "Player" } }),
            JsonSerializer.Serialize(new { type = "snapshot", captured_at = now, active_player = new { level = 10 } }),
            "invalid-json",
            JsonSerializer.Serialize(new { type = "snapshot", captured_at = now.AddMinutes(5) })
        });
        var window = TelemetryReader.ReadTelemetryWindow(path, now.AddSeconds(-1), now.AddSeconds(1));
        Assert.False(window.Truncated);
        Assert.Equal(2, window.Records.Count);
        Assert.IsType<Dictionary<string, object>>(window.Records[1]["active_player"]);
    }

    [FfmpegFact]
    public async Task ProcessRunner_CancellationTerminatesFfmpeg()
    {
        var executable = FfmpegTestTools.FindExecutable()!;
        var start = new System.Diagnostics.ProcessStartInfo(executable);
        foreach (var arg in new[] { "-re", "-f", "lavfi", "-i", "color=s=32x32:r=1", "-f", "null", "-" })
            start.ArgumentList.Add(arg);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(start,
            TimeSpan.FromSeconds(5), cancellation.Token));
    }
}

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rift_test_{Guid.NewGuid():N}");
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
