using System.Diagnostics;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

[Collection("ScreenCapture")]
public sealed class WindowCaptureTests
{
    [FfmpegFact]
    [Trait("Category", "WindowCapture")]
    public async Task InvalidWindow_FailsDuringStartAndRetainsUsefulFfmpegDiagnostics()
    {
        var config = new AppConfig { FfmpegPath = FfmpegTestTools.FindExecutable()!, Encoder = "x264" };
        var ring = new MemoryRingBuffer(60, 64 * 1024 * 1024);
        var clock = new RecordingClock();
        clock.Start();
        using var pipeline = new FfmpegPipeline(ring, config, clock);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.StartAsync(
            new(1, new(0, 0, 320, 180), "x264", "p1", 10, "1M", false, false, true, "192k", 2)));
        Assert.Contains("gfxcapture", failure.Message);
        Assert.Contains("Failed to setup graphics capture", failure.Message);
        Assert.False(pipeline.IsRunning);
        Assert.Equal(0, ring.MemoryUsage);
    }

    [FfmpegTheory]
    [InlineData(true, false, true, "x264")]
    [InlineData(false, true, true, "x264")]
    [InlineData(true, true, true, "x264")]
    [InlineData(true, true, false, "x264")]
    [InlineData(true, false, true, "auto")]
    [Trait("Category", "WindowCapture")]
    public async Task ProductionPipeline_WindowAudioHighlightEvent_ExportsPlayableMp4(bool gameAudio, bool microphone,
        bool separateTracks, string encoder)
    {
        using var window = new NativeCaptureWindow();
        var hwnd = await window.Handle.WaitAsync(TimeSpan.FromSeconds(5));
        using var directory = new TestDirectory();
        var config = new AppConfig
        {
            FfmpegPath = FfmpegTestTools.FindExecutable()!,
            Encoder = encoder,
            OutputDir = directory.Path,
            PreRollSeconds = 1,
            PostRollSeconds = 0.5,
            SegmentGraceSeconds = 2
        };
        var ring = new MemoryRingBuffer(60, 64 * 1024 * 1024);
        var clock = new RecordingClock();
        clock.Start();
        using var pipeline = new FfmpegPipeline(ring, config, clock);
        using var stop = new CancellationTokenSource();
        var producer = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            var silence = new byte[480 * 2 * sizeof(float)];
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                if (gameAudio) pipeline.WriteGameAudio(silence);
                if (microphone) pipeline.WriteMicrophone(silence);
            }
        });
        try
        {
            await pipeline.StartAsync(new(hwnd, new(0, 0, 320, 180), encoder, "p1", 10, "1M",
                gameAudio, microphone, separateTracks, "192k", 2));
            // StartAsync succeeds only after the actual window has produced a complete encoded segment.
            Assert.True(ring.MemoryUsage > 188, pipeline.LastError);
            var footer = new MetadataFooterService();
            var exporter = new ExportManager(ring, footer, new StorageQuotaManager(), config);
            var clips = new ClipCoordinator(config, exporter, clock, new AppPaths());
            var player = new PlayerIdentity("CaptureTest#TAG", "CaptureTest", "TAG");
            var trigger = new HighlightEvaluator().Evaluate(new()
            {
                ["EventID"] = 1,
                ["EventName"] = "ChampionKill",
                ["EventTime"] = clock.NowSeconds,
                ["KillerName"] = player.RiotId,
                ["VictimName"] = "Enemy"
            }, player);
            Assert.NotNull(trigger);
            clips.Trigger(trigger, player);
            var readyAt = clips.ActiveWindow!.EndsAt + config.SegmentGraceSeconds;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var poll = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (clock.NowSeconds < readyAt && pipeline.IsRunning)
                await poll.WaitForNextTickAsync(deadline.Token);
            Assert.True(pipeline.IsRunning, pipeline.LastError);
            var output = await clips.TickAsync(clock.NowSeconds);
            Assert.NotNull(output);
            Assert.Equal("highlight", footer.ReadMetadataFooter(output)!["type"]);
            var decode = new List<string> { "-hide_banner", "-loglevel", "error", "-xerror", "-i", output,
                "-map", "0:v:0", "-map", "0:a:0" };
            if (gameAudio && microphone && separateTracks) decode.AddRange(["-map", "0:a:1"]);
            decode.AddRange(["-f", "null", "-"]);
            await FfmpegTestTools.RunAsync(decode.ToArray());
        }
        finally
        {
            stop.Cancel();
            try { await producer; } catch (OperationCanceledException) { }
            await pipeline.StopAsync();
        }
    }

    [FfmpegFact]
    [Trait("Category", "WindowCapture")]
    public async Task ProductionCaptureArguments_RecordRealWindowThroughBundledFfmpeg()
    {
        using var window = new NativeCaptureWindow();
        var hwnd = await window.Handle.WaitAsync(TimeSpan.FromSeconds(5));
        using var directory = new TestDirectory();
        var config = new AppConfig { FfmpegPath = FfmpegTestTools.FindExecutable()!, Encoder = "x264" };
        var ring = new MemoryRingBuffer(60, 64 * 1024 * 1024);
        var clock = new RecordingClock();
        using var pipeline = new FfmpegPipeline(ring, config, clock);
        var options = new CaptureOptions(hwnd, new(0, 0, 320, 180), "x264", "p1", 10, "1M",
            false, false, true, "192k", 2);
        var arguments = pipeline.BuildArguments(options, "libx264", null, null).ToList();
        arguments.RemoveAt(arguments.Count - 1);
        arguments.AddRange(["-t", "2", Path.Combine(directory.Path, "window.ts")]);
        var startInfo = new ProcessStartInfo(config.FfmpegPath);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        var result = await ProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(15), default);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.True(new FileInfo(Path.Combine(directory.Path, "window.ts")).Length > 188);
    }
}
