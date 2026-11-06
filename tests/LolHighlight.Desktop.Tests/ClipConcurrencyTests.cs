using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Services;
using Moq;

namespace LolHighlight.Desktop.Tests;

public sealed class ClipConcurrencyTests
{
    [Fact]
    public async Task FrequentEvents_NeverReexportCommittedOrInFlightScenes()
    {
        using var directory = new TestDirectory();
        var clock = new ManualClock { NowSeconds = 100 };
        var config = new AppConfig { OutputDir = directory.Path, PreRollSeconds = 45, PostRollSeconds = 15 };
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporter = new Mock<IExportManager>();
        var ranges = new List<(double Start, double End)>();
        exporter.Setup(x => x.ExportAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Callback<double, double, string, Dictionary<string, object>?, CancellationToken>((start, end, _, _, _) => ranges.Add((start, end)))
            .Returns(() => ranges.Count == 1 ? pending.Task : Task.FromResult("next.mp4"));
        var clips = new ClipCoordinator(config, exporter.Object, clock, new AppPaths(directory.Path));
        clips.Trigger(Trigger(1, clock), new("Player#TAG", "Player"));
        var first = clips.TickAsync(118);
        clock.NowSeconds = 119;
        clips.Trigger(Trigger(2, clock), new("Player#TAG", "Player"));
        Assert.Equal(115, clips.ActiveWindow!.StartedAt);
        // The last GOP extends past the requested end. Its frames must not be reused either.
        exporter.SetupGet(x => x.LastExportedEnd).Returns(116d);
        pending.SetResult("first.mp4"); await first;
        Assert.Equal(116, clips.ActiveWindow!.StartedAt);
        clock.NowSeconds = 121;
        clips.Trigger(Trigger(3, clock), new("Player#TAG", "Player"));
        await clips.TickAsync(140);
        Assert.Equal(2, ranges.Count);
        Assert.True(ranges[1].Start >= 116);
        // An already exported event cannot create another clip when polled again.
        clips.Trigger(Trigger(1, clock), new("Player#TAG", "Player"));
        Assert.Null(clips.ActiveWindow);
        clips.Reset();
        clips.Trigger(Trigger(1, clock), new("Player#TAG", "Player"));
        Assert.NotNull(clips.ActiveWindow);
    }

    [Fact]
    public async Task EventAfterCompletedExport_ClampsPreRollToPreviousEnd()
    {
        using var directory = new TestDirectory();
        var clock = new ManualClock { NowSeconds = 100 };
        var exporter = new Mock<IExportManager>();
        exporter.Setup(x => x.ExportAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>())).ReturnsAsync("saved.mp4");
        var clips = new ClipCoordinator(new AppConfig(), exporter.Object, clock, new AppPaths(directory.Path));
        clips.Trigger(Trigger(1, clock), new("Player#TAG", "Player"));
        await clips.TickAsync(118);
        clock.NowSeconds = 120;
        clips.Trigger(Trigger(2, clock), new("Player#TAG", "Player"));
        Assert.Equal(115, clips.ActiveWindow!.StartedAt);
    }

    [Fact]
    public async Task FailedExport_DoesNotOverwriteNewHighlights()
    {
        using var directory = new TestDirectory();
        var clock = new ManualClock { NowSeconds = 50 };
        var config = new AppConfig { OutputDir = directory.Path, PreRollSeconds = 5, PostRollSeconds = 3 };
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporter = new Mock<IExportManager>();
        exporter.SetupSequence(x => x.ExportAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Returns(failed.Task).ReturnsAsync("retry.mp4").ReturnsAsync("next.mp4");
        var clips = new ClipCoordinator(config, exporter.Object, clock, new AppPaths());
        clips.Trigger(Trigger(1, clock), new("Player#TAG", "Player"));
        var first = clips.TickAsync(60);
        clock.NowSeconds = 61;
        clips.Trigger(Trigger(2, clock), new("Player#TAG", "Player"));
        failed.SetException(new IOException("disk busy"));
        await Assert.ThrowsAsync<IOException>(() => first);
        Assert.Equal(1, clips.ActiveWindow!.Triggers.Single().EventId);
        await clips.TickAsync(70, force: true);
        Assert.Equal(2, clips.ActiveWindow!.Triggers.Single().EventId);
        await clips.TickAsync(70, force: true);
        Assert.Null(clips.ActiveWindow);
    }

    [Fact]
    public async Task LongFight_IsSplitBeforeRingEviction_AndNewTriggersDoNotRewindContinuation()
    {
        using var directory = new TestDirectory();
        var clock = new ManualClock { NowSeconds = 100 };
        var config = new AppConfig { OutputDir = directory.Path };
        var exporter = new Mock<IExportManager>();
        exporter.Setup(x => x.ExportAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>())).ReturnsAsync("part.mp4");
        var clips = new ClipCoordinator(config, exporter.Object, clock, new AppPaths());
        clips.Trigger(Trigger(1, clock), new("Player#TAG", "Player"));
        clock.NowSeconds = 110;
        clips.Trigger(Trigger(2, clock), new("Player#TAG", "Player"));
        await clips.TickAsync(118);
        Assert.True(clips.ActiveWindow!.IsContinuation);
        var continuationStart = clips.ActiveWindow.StartedAt;
        clock.NowSeconds = 119;
        clips.Trigger(Trigger(3, clock), new("Player#TAG", "Player"));
        Assert.Equal(continuationStart, clips.ActiveWindow.StartedAt);
        Assert.True(continuationStart >= 113);
    }

    private static HighlightTrigger Trigger(int id, ManualClock clock) =>
        new(id, "kill", "击杀", clock.NowSeconds, clock.StartedAtUtc.AddSeconds(clock.NowSeconds), new());

    private sealed class ManualClock : IRecordingClock
    {
        public bool IsRunning => true;
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public double NowSeconds { get; set; }
        public double ToSessionSeconds(DateTime utcTime) => (utcTime - StartedAtUtc).TotalSeconds;
        public void Start() { }
        public void Stop() { }
    }
}
