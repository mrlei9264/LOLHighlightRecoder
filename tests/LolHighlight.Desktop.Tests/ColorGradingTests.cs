using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public sealed class ColorGradingTests
{
    [FfmpegFact]
    public async Task EveryTone_RendersFullVideo_PreservesResolutionAudioAndTelemetry()
    {
        using var d = new TestDirectory();
        var source = Path.Combine(d.Path, "原片 with spaces.mp4");
        await FfmpegTestTools.RunAsync("-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30",
            "-f", "lavfi", "-i", "sine=frequency=660:sample_rate=48000", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
            "-t", "1.2", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-c:a", "aac", source);
        var telemetry = Path.Combine(d.Path, "data.ndjson");
        await File.WriteAllTextAsync(telemetry, "{\"type\":\"snapshot\",\"captured_at\":\"2026-11-04T00:00:00Z\",\"value\":42}\n");
        var metadata = new MetadataFooterService();
        var archive = await TelemetryArchive.AppendAsync(source, new(telemetry, DateTime.MinValue, DateTime.MaxValue));
        metadata.AppendMetadataFooter(source, new() { ["type"] = "highlight", ["telemetry_archive"] = archive,
            ["triggers"] = new object[] { new Dictionary<string, object> { ["kind"] = "kill", ["label"] = "击杀" } } });
        var original = await File.ReadAllBytesAsync(source);
        var originalAudio = Path.Combine(d.Path, "original.aac");
        await FfmpegTestTools.RunAsync("-v", "error", "-i", source, "-map", "0:a:0", "-c:a", "copy", "-f", "adts", originalAudio);
        using var service = new ColorGradingService(new AppConfig { FfmpegPath = FfmpegTestTools.FindExecutable()!, RuntimeDir = d.Path }, new AppPaths(), metadata);
        foreach (var tone in ColorTones.All)
        {
            var output = Path.Combine(d.Path, tone.Id + ".mp4");
            await service.RenderAsync(new(source, output, tone.Id));
            var probe = await service.ProbeAsync(output);
            Assert.Equal(320, probe.Width); Assert.Equal(180, probe.Height);
            Assert.InRange(probe.Duration, 1.19, 1.3); Assert.Equal(2, probe.AudioCodecs.Length);
            using var records = new MemoryStream();
            await TelemetryArchive.ExtractAsync(output, records);
            Assert.Equal(await File.ReadAllTextAsync(telemetry), System.Text.Encoding.UTF8.GetString(records.ToArray()));
            Assert.Equal("highlight", metadata.ReadMetadataFooter(output)!["type"]);
            Assert.NotNull(metadata.ReadMetadataFooter(output)!["color_grade"]);
            await FfmpegTestTools.RunAsync("-v", "error", "-xerror", "-i", output, "-f", "null", "-");
        }
        var coloredAudio = Path.Combine(d.Path, "colored.aac");
        await FfmpegTestTools.RunAsync("-v", "error", "-i", Path.Combine(d.Path, "cool.mp4"), "-map", "0:a:0", "-c:a", "copy", "-f", "adts", coloredAudio);
        Assert.Equal(await File.ReadAllBytesAsync(originalAudio), await File.ReadAllBytesAsync(coloredAudio));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(d.Path, "*.part.mp4"));
    }

    [FfmpegFact]
    public async Task VideoWithoutAudio_IsNotGivenASyntheticAudioTrack()
    {
        using var d = new TestDirectory();
        var source = Path.Combine(d.Path, "silent.mp4");
        await FfmpegTestTools.RunAsync("-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24", "-t", "1", "-c:v", "libx264", source);
        using var service = Service(d.Path);
        var output = Path.Combine(d.Path, "color.mp4");
        await service.RenderAsync(new(source, output, "mono"));
        Assert.Empty((await service.ProbeAsync(output)).AudioCodecs);
    }

    [FfmpegFact]
    public async Task ExistingFilesAndCancellation_DoNotDeleteOrOverwriteUserData()
    {
        using var d = new TestDirectory();
        var source = Path.Combine(d.Path, "source.mp4"); await File.WriteAllTextAsync(source, "keep-source");
        var output = Path.Combine(d.Path, "out.mp4"); await File.WriteAllTextAsync(output + ".part.mp4", "keep-part");
        using var service = Service(d.Path);
        await Assert.ThrowsAsync<IOException>(() => service.RenderAsync(new(source, output, "cool")));
        Assert.Equal("keep-part", await File.ReadAllTextAsync(output + ".part.mp4"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenderAsync(new(source, source, "cool")));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RenderAsync(new(source, output, "cool"), token: cancel.Token));
        Assert.Equal("keep-source", await File.ReadAllTextAsync(source));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-.1)]
    [InlineData(1.1)]
    public void InvalidStrength_IsRejected(double strength) => Assert.Throws<ArgumentOutOfRangeException>(() => ColorGradeLut.CreateRgb(ColorTones.Get("cool"), strength));

    private static ColorGradingService Service(string root) => new(new AppConfig { FfmpegPath = FfmpegTestTools.FindExecutable()!, RuntimeDir = root }, new AppPaths(), new MetadataFooterService());
}
