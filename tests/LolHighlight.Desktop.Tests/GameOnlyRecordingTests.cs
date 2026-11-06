using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;
using Moq;

namespace LolHighlight.Desktop.Tests;

public sealed class GameOnlyRecordingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"game_only_{Guid.NewGuid():N}");
    private readonly Mock<IRiotApiClient> _riot = new();
    private readonly Mock<IFfmpegPipeline> _pipeline = new();
    private readonly Mock<IGameAudioCapture> _audio = new();
    private readonly Mock<IMicrophoneCapture> _mic = new();
    private readonly Mock<IGameWindowLocator> _windows = new();
    private readonly Mock<IClipCoordinator> _clips = new();
    private readonly MemoryRingBuffer _ring = new(60, 1024 * 1024);
    private readonly RecordingClock _clock = new();
    private readonly RecordingSessionService _session;
    private readonly AppConfig _config;
    private readonly GameCaptureTarget _target = new(123, 456, new(0, 0, 1280, 720));

    public GameOnlyRecordingTests()
    {
        var config = _config = new AppConfig { OutputDir = _root, RuntimeDir = _root, MicrophoneEnabled = true };
        _riot.Setup(x => x.GetActivePlayerAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PlayerIdentity("Player#TAG", "Player", "TAG"));
        _riot.Setup(x => x.GetGameStatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<string, object> { ["gameTime"] = 100d });
        _riot.Setup(x => x.GetEventsAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<Dictionary<string, object>>());
        _riot.Setup(x => x.GetActivePlayerDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<string, object>());
        _riot.Setup(x => x.GetPlayerListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Dictionary<string, object>>());
        _audio.SetupGet(x => x.IsRunning).Returns(true);
        _mic.SetupGet(x => x.IsRunning).Returns(true);
        _session = new(config, new AppPaths(), _riot.Object, new HighlightEvaluator(),
            _clips.Object, Mock.Of<ITelemetryCollector>(), _pipeline.Object, _audio.Object,
            _mic.Object, _ring, _clock, _windows.Object);
    }

    [Fact]
    public async Task StartWithoutMatch_DoesNotCaptureOrStartClock()
    {
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _riot.Setup(x => x.GetActivePlayerAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            polled.TrySetResult();
            return Task.FromException<PlayerIdentity>(new RiotClientUnavailable("no match"));
        });
        await _session.StartAsync();
        await polled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _session.StopAsync();
        _pipeline.Verify(x => x.StartAsync(It.IsAny<CaptureOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        _audio.Verify(x => x.StartAsync(It.IsAny<int>(), It.IsAny<Action<byte[]>>()), Times.Never);
        _mic.Verify(x => x.StartAsync(It.IsAny<Action<byte[]>>()), Times.Never);
        Assert.False(_clock.IsRunning);
        Assert.Equal(0, _ring.BufferedSeconds);
    }

    [Fact]
    public async Task MatchWithoutForegroundGameWindow_DoesNotCapture()
    {
        await _session.PollGameAsync(default);
        _pipeline.Verify(x => x.StartAsync(It.IsAny<CaptureOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(RecordingStatus.WaitingForGame, _session.Snapshot.Status);
    }

    [Fact]
    public async Task MatchWithGameWindow_UsesHandleAndProcess()
    {
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        await _session.PollGameAsync(default);
        _pipeline.Verify(x => x.StartAsync(It.Is<CaptureOptions>(o => o.WindowHandle == 456 && o.CaptureRect == _target.Rect), It.IsAny<CancellationToken>()), Times.Once);
        _audio.Verify(x => x.StartAsync(123, It.IsAny<Action<byte[]>>()), Times.Once);
        Assert.Equal(RecordingStatus.Recording, _session.Snapshot.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingGameOrGameEnd_StopsCaptureFlushesHighlightAndClearsBuffer(bool gameEnd)
    {
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        await _session.PollGameAsync(default);
        _ring.PushSegment(0, 0, 1, new byte[] { 1, 2, 3 });
        _pipeline.Invocations.Clear();
        _clips.Invocations.Clear();
        if (gameEnd)
            _riot.Setup(x => x.GetEventsAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<Dictionary<string, object>> { new() { ["EventName"] = "GameEnd" } });
        else
            _windows.Setup(x => x.FindLeagueWindow()).Returns((GameCaptureTarget?)null);
        await _session.PollGameAsync(default);
        _pipeline.Verify(x => x.StopAsync(), Times.Once);
        _clips.Verify(x => x.TickAsync(It.IsAny<double>(), true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, _ring.BufferedSeconds);
        Assert.False(_clock.IsRunning);
        Assert.Equal(RecordingStatus.WaitingForGame, _session.Snapshot.Status);
    }

    [Fact]
    public async Task ReturningToGame_StartsFreshCapture()
    {
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        await _session.PollGameAsync(default);
        _windows.Setup(x => x.FindLeagueWindow()).Returns((GameCaptureTarget?)null);
        await _session.PollGameAsync(default);
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        await _session.PollGameAsync(default);
        _pipeline.Verify(x => x.StartAsync(It.IsAny<CaptureOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LaterPoll_RetainsTeamForAceRecognition()
    {
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        _riot.Setup(x => x.GetPlayerListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Dictionary<string, object>>
        {
            new() { ["riotIdGameName"] = "Player", ["riotId"] = "Player#OTHER", ["team"] = "CHAOS" },
            new() { ["riotIdGameName"] = "Player", ["riotId"] = "Player#TAG", ["team"] = "ORDER" }
        });
        await _session.PollGameAsync(default);
        _riot.Setup(x => x.GetEventsAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<Dictionary<string, object>>
        {
            new() { ["EventName"] = "Ace", ["EventID"] = 5, ["AcingTeam"] = "ORDER" }
        });
        await _session.PollGameAsync(default);
        _clips.Verify(x => x.Trigger(It.Is<HighlightTrigger>(trigger => trigger.Kind == "ace"),
            It.Is<PlayerIdentity>(player => player.Team == "ORDER")), Times.Once);
    }

    [Fact]
    public async Task GameEndResponse_ProcessesFinalKillBeforeFlushing()
    {
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        await _session.PollGameAsync(default);
        _riot.Setup(x => x.GetEventsAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<Dictionary<string, object>>
        {
            new() { ["EventName"] = "ChampionKill", ["EventID"] = 5, ["KillerName"] = "Player", ["VictimName"] = "Enemy" },
            new() { ["EventName"] = "GameEnd", ["EventID"] = 6 }
        });
        await _session.PollGameAsync(default);
        _clips.Verify(x => x.Trigger(It.Is<HighlightTrigger>(trigger => trigger.Kind == "kill"),
            It.IsAny<PlayerIdentity>()), Times.Once);
    }

    [Fact]
    public async Task BackgroundGame_IsDetectedButDoesNotCapture_AndResumesWhenFocused()
    {
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target with { CanCapture = false });
        await _session.PollGameAsync(default);
        Assert.Equal(RecordingStatus.Paused, _session.Snapshot.Status);
        Assert.Equal("Player", _session.Snapshot.Player?.GameName);
        _pipeline.Verify(x => x.StartAsync(It.IsAny<CaptureOptions>(), It.IsAny<CancellationToken>()), Times.Never);

        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        await _session.PollGameAsync(default);
        Assert.Equal(RecordingStatus.Recording, _session.Snapshot.Status);
    }

    [Fact]
    public async Task CaptureStartupFailure_KeepsListeningAndRetriesAutomatically()
    {
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        var attempts = 0;
        var running = false;
        _pipeline.SetupGet(x => x.IsRunning).Returns(() => running);
        _pipeline.Setup(x => x.StopAsync()).Callback(() => running = false).Returns(Task.CompletedTask);
        _pipeline.Setup(x => x.StartAsync(It.IsAny<CaptureOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (++attempts == 1) return Task.FromException(new IOException("temporary capture failure"));
                running = true;
                return Task.CompletedTask;
            });
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retrying = false;
        _session.StatusChanged += (_, e) =>
        {
            if (e.Snapshot.Status == RecordingStatus.Retrying)
            {
                retrying = true;
                Assert.Equal("Player", e.Snapshot.Player?.GameName);
            }
            if (e.Snapshot.Status == RecordingStatus.Recording) recovered.TrySetResult();
        };
        await _session.StartAsync();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(retrying);
        Assert.Equal(2, attempts);
        await _session.StopAsync();
    }

    [Fact]
    public async Task MissingMicrophone_DoesNotPreventGameVideoAndAudioCapture()
    {
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        _mic.Setup(x => x.StartAsync(It.IsAny<Action<byte[]>>())).ThrowsAsync(
            new System.Runtime.InteropServices.COMException("Element not found", unchecked((int)0x80070490)));
        await _session.PollGameAsync(default);
        _pipeline.Verify(x => x.StartAsync(It.Is<CaptureOptions>(options => options.WithGameAudio && !options.WithMicrophone),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(RecordingStatus.Recording, _session.Snapshot.Status);
        Assert.Contains("已跳过麦克风", _session.Snapshot.LastError);
    }

    [Fact]
    public void Pipeline_UsesWindowInputAndRejectsDesktop()
    {
        using var pipeline = new FfmpegPipeline(_ring, new AppConfig(), _clock);
        var options = new CaptureOptions(_target.WindowHandle, _target.Rect, "x264", "p1", 30, "8M", false, false, false, "192k", 2);
        var arguments = pipeline.BuildArguments(options, "libx264", null, null).ToArray();
        Assert.Contains(arguments, argument => argument.StartsWith("gfxcapture=hwnd=456:") && argument.Contains("monitor_idx=window"));
        Assert.Contains("hwdownload,format=bgra,format=yuv420p", arguments);
        Assert.DoesNotContain("gdigrab", arguments);
        Assert.DoesNotContain("desktop", arguments);
        Assert.Throws<InvalidOperationException>(() => pipeline.BuildArguments(options with { WindowHandle = 0 }, "libx264", null, null).ToArray());
    }

    [Fact]
    public async Task SlowExport_DoesNotBlockSubsequentGamePoll()
    {
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        var running = false;
        _pipeline.SetupGet(x => x.IsRunning).Returns(() => running);
        _pipeline.Setup(x => x.StartAsync(It.IsAny<CaptureOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => running = true).Returns(Task.CompletedTask);
        _pipeline.Setup(x => x.StopAsync()).Callback(() => running = false).Returns(Task.CompletedTask);
        var exporting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clips.Setup(x => x.TickAsync(It.IsAny<double>(), false, It.IsAny<CancellationToken>())).Returns(() =>
        {
            exporting.TrySetResult();
            return release.Task;
        });
        var secondPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polls = 0;
        _riot.Setup(x => x.GetActivePlayerAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (Interlocked.Increment(ref polls) >= 2) secondPoll.TrySetResult();
            return Task.FromResult(new PlayerIdentity("Player#TAG", "Player", "TAG"));
        });
        try
        {
            await _session.StartAsync();
            await exporting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await secondPoll.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(release.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult(null);
            await _session.StopAsync();
        }
    }

    [Fact]
    public async Task EventSelection_OnlyTriggersSelectedKinds_AndDeduplicatesNotifications()
    {
        _config.RecordingEvents = ["multikill_5"];
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        await _session.PollGameAsync(default);
        var notifications = new List<string>();
        _session.HighlightDetected += (_, e) => notifications.Add(e.Trigger.Kind);
        _session.HighlightDetected += (_, _) => throw new InvalidOperationException("UI failed");
        _riot.Setup(x => x.GetEventsAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<Dictionary<string, object>>
        {
            new() { ["EventID"] = 1, ["EventName"] = "ChampionKill", ["KillerName"] = "Player" },
            new() { ["EventID"] = 2, ["EventName"] = "Multikill", ["KillerName"] = "Player", ["KillStreak"] = 5 }
        });
        await _session.PollGameAsync(default);
        await _session.PollGameAsync(default);
        Assert.Equal(new[] { "multikill_5" }, notifications);
        _clips.Verify(x => x.Trigger(It.Is<HighlightTrigger>(trigger => trigger.Kind == "kill"), It.IsAny<PlayerIdentity>()), Times.Never);
        _clips.Verify(x => x.Trigger(It.Is<HighlightTrigger>(trigger => trigger.Kind == "multikill_5"), It.IsAny<PlayerIdentity>()), Times.Once);
        _pipeline.Verify(x => x.StartAsync(It.IsAny<CaptureOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _pipeline.Verify(x => x.StopAsync(), Times.Once); // Initial empty-session cleanup only.
    }

    [Fact]
    public async Task FullGameMode_IgnoresEventSelectionAndBackgroundChange_AndUsesOneContinuousFile()
    {
        _config.RecordingMode = "full_game";
        _config.RecordingEvents = [];
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        await _session.PollGameAsync(default);
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target with { CanCapture = false });
        await _session.PollGameAsync(default);
        _pipeline.Verify(x => x.StartAsync(It.Is<CaptureOptions>(options => options.FullGameOutputPath != null &&
            Path.GetFileName(options.FullGameOutputPath).StartsWith("fullgame_")), It.IsAny<CancellationToken>()), Times.Once);
        _pipeline.Verify(x => x.StopAsync(), Times.Once); // No restart on foreground changes.
        _clips.Verify(x => x.Trigger(It.IsAny<HighlightTrigger>(), It.IsAny<PlayerIdentity>()), Times.Never);
    }

    [Fact]
    public async Task DisplayMode_UsesActualGameMonitorAndDimensions()
    {
        _config.CaptureMode = "display";
        var monitor = new CaptureRect(1920, 0, 2560, 1440);
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target with { MonitorHandle = 1234, MonitorRect = monitor });
        await _session.PollGameAsync(default);
        _pipeline.Verify(x => x.StartAsync(It.Is<CaptureOptions>(options => options.CaptureMode == "display" &&
            options.MonitorHandle == 1234 && options.CaptureRect == monitor), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FullGameStop_PublishesFileOnlyAfterMetadataIsComplete()
    {
        _config.RecordingMode = "full_game";
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        string? pending = null;
        _pipeline.Setup(x => x.StartAsync(It.IsAny<CaptureOptions>(), It.IsAny<CancellationToken>()))
            .Callback<CaptureOptions, CancellationToken>((options, _) => pending = options.FullGameOutputPath).Returns(Task.CompletedTask);
        _pipeline.Setup(x => x.StopAsync()).Returns(async () =>
        {
            if (pending == null) return;
            await File.WriteAllBytesAsync(pending, new byte[] { 0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p' });
            pending = null;
        });
        await _session.PollGameAsync(default);
        Assert.Empty(new VideoScanner().ScanVideos(_root));
        await _session.StopAsync();
        var result = Assert.Single(new VideoScanner().ScanVideos(_root));
        Assert.StartsWith("fullgame_", result.Name);
        Assert.Equal("full_game", new MetadataFooterService().ReadMetadataFooter(result.Path)!["type"]);
        Assert.DoesNotContain(Directory.GetFiles(_root), path => path.EndsWith(".part.mp4"));
    }

    [Fact]
    public async Task SnapshotApi_UsesOneRequestPerPoll_InsteadOfRepeatedSubsetQueries()
    {
        var riot = new Mock<IRiotApiClient>();
        var source = riot.As<ILiveGameSnapshotSource>();
        source.Setup(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new LiveGameData(new()
        {
            ["activePlayer"] = new Dictionary<string, object> { ["riotId"] = "Player#TAG", ["riotIdGameName"] = "Player" },
            ["gameData"] = new Dictionary<string, object> { ["gameTime"] = 100d },
            ["allPlayers"] = new List<Dictionary<string, object>>()
        }));
        _windows.Setup(x => x.FindLeagueWindow()).Returns(_target);
        using var session = new RecordingSessionService(_config, new AppPaths(), riot.Object, new HighlightEvaluator(),
            _clips.Object, Mock.Of<ITelemetryCollector>(), _pipeline.Object, _audio.Object, _mic.Object, _ring, _clock, _windows.Object);
        await session.PollGameAsync(default);
        await session.PollGameAsync(default);
        source.Verify(x => x.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        riot.Verify(x => x.GetActivePlayerAsync(It.IsAny<CancellationToken>()), Times.Never);
        riot.Verify(x => x.GetPlayerListAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
