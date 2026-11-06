using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public class ClipCoordinatorTests
{
    private readonly AppConfig _config = new();
    private readonly FakeRecordingClock _clock = new();
    private readonly ClipCoordinator _coordinator;

    public ClipCoordinatorTests()
    {
        _coordinator = new ClipCoordinator(_config, new NoopExportManager(), _clock, new AppPaths());
    }

    [Fact]
    public void Trigger_NewHighlight_CreatesWindow()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        var trigger = CreateTrigger(1, "kill", "击杀 Enemy", 10.0);

        _coordinator.Trigger(trigger, player);

        Assert.NotNull(_coordinator.ActiveWindow);
        Assert.Single(_coordinator.ActiveWindow.Triggers);
        Assert.Equal("kill", _coordinator.ActiveWindow.Triggers[0].Kind);
        Assert.Equal(0, _coordinator.ActiveWindow.StartedAt);
        Assert.Equal(_config.PostRollSeconds, _coordinator.ActiveWindow.EndsAt);
    }

    [Fact]
    public void Trigger_ExistingWindow_ExtendsWindow()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        _coordinator.Trigger(CreateTrigger(1, "kill", "击杀 Enemy", 10.0), player);
        _clock.NowSeconds = 10;
        _coordinator.Trigger(CreateTrigger(2, "ace", "团灭", 20.0), player);

        Assert.NotNull(_coordinator.ActiveWindow);
        Assert.Equal(2, _coordinator.ActiveWindow.Triggers.Count);
        Assert.True(_coordinator.ActiveWindow.EndsAt > 10);
    }

    [Fact]
    public void Trigger_DuplicateEventId_DoesNotAddDuplicate()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        _coordinator.Trigger(CreateTrigger(1, "kill", "击杀 Enemy", 10.0), player);
        _coordinator.Trigger(CreateTrigger(1, "kill", "击杀 Enemy", 10.0), player);

        Assert.Single(_coordinator.ActiveWindow!.Triggers);
    }

    [Fact]
    public async Task Tick_NoActiveWindow_ReturnsNull()
    {
        var result = await _coordinator.TickAsync(0);

        Assert.Null(result);
    }

    [Fact]
    public async Task Tick_BeforeReadyAt_ReturnsNull()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        _coordinator.Trigger(CreateTrigger(1, "kill", "击杀 Enemy", 10.0), player);

        var result = await _coordinator.TickAsync(1);

        Assert.Null(result);
        Assert.NotNull(_coordinator.ActiveWindow);
    }

    [Fact]
    public async Task Tick_ForceExport_ReturnsResult()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        _coordinator.Trigger(CreateTrigger(1, "kill", "击杀 Enemy", 10.0), player);

        var result = await _coordinator.TickAsync(0, force: true);

        Assert.NotNull(result);
        Assert.Null(_coordinator.ActiveWindow);
    }

    [Fact]
    public void Reset_DropsPendingWindowForNewCaptureTimeline()
    {
        _coordinator.Trigger(CreateTrigger(1, "kill", "击杀", 10), new("Player#TAG", "Player"));
        _coordinator.Reset();
        Assert.Null(_coordinator.ActiveWindow);
    }

    private static HighlightTrigger CreateTrigger(int eventId, string kind, string label, double eventTime)
        => new(eventId, kind, label, eventTime, DateTime.UtcNow, new Dictionary<string, object>());

    private sealed class FakeRecordingClock : IRecordingClock
    {
        public bool IsRunning => true;
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public double NowSeconds { get; set; }
        public void Start() { }
        public void Stop() { }
        public double ToSessionSeconds(DateTime utcTime) => NowSeconds;
    }

    private sealed class NoopExportManager : IExportManager
    {
        public Task<string> ExportAsync(double startTime, double endTime, string outputPath,
            Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
            => Task.FromResult(outputPath);
    }
}
