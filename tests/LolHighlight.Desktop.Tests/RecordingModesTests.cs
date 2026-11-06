using System.Text;
using System.Text.Json;
using LolHighlight.Desktop.ViewModels;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public sealed class RecordingModesTests
{
    [Fact]
    public void ModesAndEventSelection_PersistToLiveConfigAndReloadWithoutAliasing()
    {
        using var directory = new TestDirectory();
        var config = new AppConfig();
        var service = new SettingsService(Path.Combine(directory.Path, "config.json"), config);
        using var vm = new SettingsViewModel(service, config);
        foreach (var choice in vm.RecordingEvents) choice.IsSelected = choice.Id is "assist" or "multikill_5";
        vm.RecordingMode = "full_game";
        vm.CaptureMode = "display";
        vm.TelemetryInterval = 0.1;
        vm.ShowEventNotifications = false;
        vm.SaveCommand.Execute(null);
        Assert.False(vm.SaveFailed, vm.SaveMessage);
        Assert.Equal("display", config.CaptureMode);
        Assert.Equal("full_game", config.RecordingMode);
        Assert.Equal(new[] { "multikill_5", "assist" }, config.RecordingEvents);
        Assert.False(config.ShowEventNotifications);
        Assert.Equal(0.1, config.PollIntervalSeconds);
        var copy = config.Clone();
        copy.RecordingEvents[0] = "kill";
        Assert.Equal("multikill_5", config.RecordingEvents[0]);
        using var reloaded = new SettingsViewModel(service, config);
        Assert.False(reloaded.HasUnsavedChanges);
        Assert.Equal(vm.CaptureMode, reloaded.CaptureMode);
        Assert.Equal(vm.RecordingMode, reloaded.RecordingMode);
    }

    [Fact]
    public void EmptyEventSelection_IsAllowedInFullGameModeAndExplainedInHighlightMode()
    {
        using var directory = new TestDirectory();
        var config = new AppConfig();
        using var vm = new SettingsViewModel(new SettingsService(Path.Combine(directory.Path, "config.json"), config), config);
        foreach (var choice in vm.RecordingEvents) choice.IsSelected = false;
        Assert.False(vm.CanSave);
        Assert.Contains("至少", vm.SettingsStateText);
        vm.RecordingMode = "full_game";
        Assert.True(vm.CanSave);
        vm.SaveCommand.Execute(null);
        Assert.Empty(config.RecordingEvents);
    }

    [Fact]
    public async Task TelemetryArchive_RoundTripsBeyondOld16MbLimit_AndRetainsUnknownFields()
    {
        using var directory = new TestDirectory();
        var spool = Path.Combine(directory.Path, "telemetry.ndjson");
        var video = Path.Combine(directory.Path, "test.mp4");
        var time = DateTime.UtcNow;
        using (var collector = new TelemetryCollector(new("A#B", "A", "B")))
        {
            collector.Start(directory.Path, time, new(), new(), new());
            for (var i = 0; i < 5000; i++)
                collector.CaptureAll(time.AddMilliseconds(i * 100), new(new()
                {
                    ["new_future_api_field"] = new string('x', 4000),
                    ["sequence"] = i
                }));
        }
        Assert.True(new FileInfo(spool).Length > 16 * 1024 * 1024);
        await File.WriteAllBytesAsync(video, new byte[] { 0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p' });
        var archive = await TelemetryArchive.AppendAsync(video, new(spool, time, time.AddMinutes(10)));
        Assert.True(archive.Chunks > 16);
        Assert.Equal(5002, archive.Records);
        using var extracted = new MemoryStream();
        await TelemetryArchive.ExtractAsync(video, extracted);
        Assert.Equal(await File.ReadAllBytesAsync(spool), extracted.ToArray());
        var footer = new MetadataFooterService();
        footer.AppendMetadataFooter(video, new() { ["type"] = "full_game" });
        Assert.Equal("full_game", footer.ReadMetadataFooter(video)!["type"]);
    }

    [Fact]
    public async Task TelemetryArchive_OnlyIncludesClipWindowPlusSessionContext()
    {
        using var directory = new TestDirectory();
        var spool = Path.Combine(directory.Path, "telemetry.ndjson");
        var video = Path.Combine(directory.Path, "test.mp4");
        var time = DateTime.UtcNow;
        using (var collector = new TelemetryCollector(new("A#B", "A", "B")))
        {
            collector.Start(directory.Path, time, new(), new(), new());
            collector.CaptureAll(time.AddSeconds(5), new(new() { ["included"] = true }));
            collector.CaptureAll(time.AddSeconds(20), new(new() { ["excluded"] = true }));
        }
        await File.WriteAllBytesAsync(video, []);
        var result = await TelemetryArchive.AppendAsync(video, new(spool, time.AddSeconds(4), time.AddSeconds(6)));
        Assert.Equal(2, result.Records);
        using var output = new MemoryStream();
        await TelemetryArchive.ExtractAsync(video, output);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("included", text);
        Assert.DoesNotContain("excluded", text);
    }

    [FfmpegFact]
    public async Task ContinuousRecording_StreamsMoreThanRingRetentionWithoutEvents_AndDecodesEveryFrame()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Path, "input.ts");
        var video = Path.Combine(directory.Path, "fullgame_test.mp4");
        await FfmpegTestTools.RunAsync("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=10",
            "-t", "12", "-c:v", "libx264", "-preset", "ultrafast", "-g", "10", "-bf", "0", "-f", "mpegts", source);
        var config = new AppConfig { FfmpegPath = FfmpegTestTools.FindExecutable()!, PreRollSeconds = 1, PostRollSeconds = 1, MinimumFreeGb = 1 };
        await using (var recording = new ContinuousRecording(config, video))
        {
            recording.Start();
            await using var input = File.OpenRead(source);
            var buffer = new byte[4096];
            int count;
            while ((count = await input.ReadAsync(buffer)) > 0) await recording.WriteAsync(buffer.AsMemory(0, count), default);
        }
        var frames = Path.Combine(directory.Path, "frames.md5");
        await FfmpegTestTools.RunAsync("-hide_banner", "-loglevel", "error", "-xerror", "-i", video, "-f", "framemd5", frames);
        Assert.Equal(120, File.ReadLines(frames).Count(line => !line.StartsWith('#') && line.Length > 0));
        Assert.False(File.Exists(video + ".part.mp4"));
    }

    [Fact]
    public async Task TelemetryArchive_RollsBackPartialAppendWhenStorageReservationFails()
    {
        using var directory = new TestDirectory();
        var video = Path.Combine(directory.Path, "test.mp4");
        var original = new byte[] { 0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p' };
        await File.WriteAllBytesAsync(video, original);
        var time = DateTime.UtcNow;
        using (var collector = new TelemetryCollector(new("A#B", "A", "B")))
        {
            collector.Start(directory.Path, time, new(), new(), new());
            for (var i = 0; i < 2500; i++) collector.CaptureAll(time, new(new() { ["field"] = new string('x', 1000) }));
        }
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => TelemetryArchive.AppendAsync(video,
            new(Path.Combine(directory.Path, "telemetry.ndjson"), time, time.AddSeconds(1)),
            reserveBytes: _ => { if (++calls > 1) throw new IOException("disk full"); }));
        Assert.True(calls > 1);
        Assert.Equal(original, await File.ReadAllBytesAsync(video));
    }
}
