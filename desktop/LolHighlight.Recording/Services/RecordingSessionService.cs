using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;

namespace LolHighlight.Recording.Services;

public interface IRecordingSessionService : IDisposable
{
    event EventHandler<RecordingStatusChangedEventArgs>? StatusChanged;
    event EventHandler<HighlightDetectedEventArgs>? HighlightDetected;
    RecordingSnapshot Snapshot { get; }
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}

public sealed class RecordingSessionService : IRecordingSessionService
{
    private readonly AppConfig _config;
    private readonly AppPaths _paths;
    private readonly IRiotApiClient _riotApi;
    private readonly IHighlightEvaluator _evaluator;
    private readonly IClipCoordinator _clipCoordinator;
    private readonly ITelemetryCollector _telemetry;
    private readonly IFfmpegPipeline _pipeline;
    private readonly IGameAudioCapture _gameAudio;
    private readonly IMicrophoneCapture _microphone;
    private readonly IMemoryRingBuffer _ringBuffer;
    private readonly IRecordingClock _clock;
    private readonly IGameWindowLocator _windows;
    private GameCaptureTarget? _captureTarget;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateLock = new();

    private RecordingSnapshot _snapshot = new(RecordingStatus.Idle, "未启动", "点击启动监听开始录制", 0, null, null);
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private Task<string?>? _exportTask;
    private bool _capturingGameAudio;
    private bool _capturingMicrophone;
    private bool _disposed;
    private double _captureStartedAt;
    private PlayerIdentity? _activePlayer;
    private PlayerIdentity? _detectedPlayer;
    private bool _telemetryStarted;
    private int? _lastEventId;
    private DateTime _nextTelemetryAt;
    private string? _sessionDirectory;
    private double _lastGameTime = -1;
    private string? _warning;
    private string? _exportWarning;
    private string? _fullGamePath;
    private readonly MatchReviewStore? _review;
    private bool _apiUnavailable;

    public event EventHandler<RecordingStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<HighlightDetectedEventArgs>? HighlightDetected;

    public RecordingSnapshot Snapshot
    {
        get { lock (_stateLock) return _snapshot; }
    }

    public bool IsRunning => _cts != null || _captureTarget != null || _pipeline.IsRunning;

    public RecordingSessionService(
        AppConfig config,
        AppPaths paths,
        IRiotApiClient riotApi,
        IHighlightEvaluator evaluator,
        IClipCoordinator clipCoordinator,
        ITelemetryCollector telemetry,
        IFfmpegPipeline pipeline,
        IGameAudioCapture gameAudio,
        IMicrophoneCapture microphone,
        IMemoryRingBuffer ringBuffer,
        IRecordingClock clock,
        IGameWindowLocator? windows = null, MatchReviewStore? review = null)
    {
        _config = config;
        _paths = paths;
        _riotApi = riotApi;
        _evaluator = evaluator;
        _clipCoordinator = clipCoordinator;
        _telemetry = telemetry;
        _pipeline = pipeline;
        _gameAudio = gameAudio;
        _microphone = microphone;
        _ringBuffer = ringBuffer;
        _clock = clock;
        _windows = windows ?? new GameWindowLocator();
        _review = review;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_monitorTask is { IsCompleted: false })
                return;

            // Error 状态仍可能保留上一次失败启动的进程、管道或取消令牌。
            // 重启前统一回收，避免旧 FFmpeg 与新会话并行运行。
            if (_cts != null || _pipeline.IsRunning)
                await StopResourcesAsync();

            _config.Validate();
            _ringBuffer.UpdateLimits(MaxBufferSeconds(),
                (_config.RecordingMode == "full_game" ? Math.Min(64, _config.MemoryBufferMb) : _config.MemoryBufferMb) * 1024L * 1024);
            _ringBuffer.Clear();
            _activePlayer = null;
            _detectedPlayer = null;
            _lastEventId = null;
            _lastGameTime = -1;
            _warning = null;
            _exportWarning = null;
            _telemetryStarted = false;
            _nextTelemetryAt = DateTime.MinValue;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SetStatus(RecordingStatus.WaitingForGame, "等待英雄联盟对局", "仅监听对局状态，尚未采集画面或音频");
            var token = _cts.Token;
            _monitorTask = Task.Run(() => MonitorLoopAsync(token), token);
        }
        catch (Exception exception)
        {
            SetStatus(RecordingStatus.Error, "启动失败", exception.Message, exception.Message);
            await StopResourcesAsync();

            _review?.CompleteMatch();
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_disposed) return;
        await _lifecycle.WaitAsync();
        try
        {
            if (_cts == null && !_pipeline.IsRunning && _captureTarget == null && !_telemetryStarted)
                return;

            SetStatus(RecordingStatus.Stopping, "正在停止录制", "正在保存缓冲区中的高光");
            _cts?.Cancel();
            if (_monitorTask != null)
            {
                try { await _monitorTask; } catch { }
                _monitorTask = null;
            }

            await StopCaptureInputsAsync();
            var exportFailed = false;
            try
            {
                if (_clipCoordinator.ActiveWindow != null)
                    SetStatus(RecordingStatus.Exporting, "正在保存最后一个高光片段", null);

                try { await CompleteExportAsync(); }
                catch (Exception error) { _warning = error.Message; }
                var exported = await FlushClipsAsync();
                if (exported != null)
                    _warning = null;
            }
            catch (Exception exception)
            {
                exportFailed = true;
                SetStatus(RecordingStatus.Error, "最后片段导出失败", exception.Message, exception.Message);
            }

            await StopResourcesAsync();
            _review?.CompleteMatch();

            if (!exportFailed)
                SetStatus(RecordingStatus.Idle, "未启动", "点击启动监听开始下一场对局");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<(bool GameAudio, bool Microphone)> StartAudioSourcesAsync(int processId)
    {
        var gameAudio = false;
        var microphone = false;

        if (_config.AudioEnabled)
        {
            try
            {
                await _gameAudio.StartAsync(processId, data => _pipeline.WriteGameAudio(data));
                gameAudio = _gameAudio.IsRunning;
            }
            catch (Exception exception)
            {
                AppendWarning($"游戏音频不可用：{exception.Message}");
                SetStatus(RecordingStatus.Starting, "游戏音频不可用，继续录制视频", "请检查默认播放设备", _warning);
            }
        }

        if (_config.MicrophoneEnabled)
        {
            try
            {
                await _microphone.StartAsync(data => _pipeline.WriteMicrophone(data));
                microphone = _microphone.IsRunning;
            }
            catch (Exception exception)
            {
                AppendWarning(exception.HResult == unchecked((int)0x80070490)
                    ? "未检测到默认麦克风，已跳过麦克风；游戏画面和游戏声音仍可录制"
                    : $"麦克风不可用，已跳过麦克风：{exception.Message}");
                SetStatus(RecordingStatus.Starting, "已跳过不可用的麦克风，继续启动采集", "可连接麦克风，或在设置中关闭麦克风录制", _warning);
            }
        }

        return (gameAudio, microphone);
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        try { RuntimeDiagnostics.PruneTelemetry(_paths.ResolveRuntimeDirectory(_config.RuntimeDir)); }
        catch (Exception error) { _warning = $"清理历史遥测失败：{error.Message}"; }
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var iterationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    if (_captureTarget != null && !_pipeline.IsRunning)
                        throw new InvalidOperationException(_pipeline.LastError ?? "FFmpeg unexpectedly stopped");
                    if (_captureTarget != null && ((_capturingGameAudio && !_gameAudio.IsRunning) ||
                        (_capturingMicrophone && !_microphone.IsRunning)))
                        throw new IOException("音频设备采集中断，正在重新连接");
                    if (_captureTarget != null && _clock.NowSeconds -
                        (_ringBuffer.SegmentCount == 0 ? _captureStartedAt : _ringBuffer.LatestTimestamp) > 15)
                        throw new IOException("视频采集长时间没有输出，正在重新初始化");

                    await PollGameAsync(cancellationToken);
                }
                catch (RiotClientUnavailable unavailable)
                {
                    var target = _windows.FindLeagueWindow();
                    if (_captureTarget != null && target?.WindowHandle == _captureTarget.WindowHandle && _pipeline.IsRunning)
                    {
                        _apiUnavailable = true;
                        SetStatus(RecordingStatus.Recording, "正在录制游戏窗口", "游戏画面持续录制，等待对局数据恢复", unavailable.Message);
                    }
                    else
                    {
                        await EndCaptureAsync();
                        if (target == null) _review?.CompleteMatch();
                        _detectedPlayer = null;
                        SetStatus(RecordingStatus.WaitingForGame, "等待英雄联盟对局", "LOL 对局接口未就绪，正在自动重试（本地端口 2999）", unavailable.Message);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    await EndCaptureAsync();
                    SetStatus(RecordingStatus.Retrying, "采集失败，正在重试",
                        $"录制或查询失败，5 秒后重试：{exception.Message}", exception.Message);
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }

                // 对局结束、Riot API 不可用时仍然要推进高光窗口，
                // 否则最后一个人头会一直卡在缓冲区里等待下一场对局。
                try
                {
                    await AdvanceExportAsync();
                    UpdateBufferedStatus();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    SetStatus(
                        _telemetryStarted ? RecordingStatus.Recording : RecordingStatus.WaitingForGame,
                        "高光导出失败，稍后重试",
                        exception.Message,
                        exception.Message);
                }

                try
                {
                    var interval = Snapshot.Status is RecordingStatus.WaitingForGame or RecordingStatus.Paused
                        ? Math.Max(1, _config.PollIntervalSeconds) : _config.PollIntervalSeconds;
                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(iterationStarted).TotalSeconds;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.005, interval - elapsed)), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            SetStatus(RecordingStatus.Error, "对局监听或录制异常", error.Message, error.Message);
        }
        finally
        {
            await StopCaptureInputsAsync();
            StopTelemetryIfNeeded();
        }
    }

    internal async Task PollGameAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        LiveGameData? fullData = null;
        PlayerIdentity player;
        Dictionary<string, object> gameStats;
        List<Dictionary<string, object>> currentEvents;
        if (_riotApi is ILiveGameSnapshotSource source)
        {
            fullData = await source.GetSnapshotAsync(cancellationToken);
            player = RiotApiClient.ParsePlayer(fullData.ActivePlayer);
            gameStats = fullData.Game;
            currentEvents = fullData.Events;
        }
        else
        {
            // Compatibility with alternate API implementations.
            var playerTask = _riotApi.GetActivePlayerAsync(cancellationToken);
            var statsTask = _riotApi.GetGameStatsAsync(cancellationToken);
            var eventsTask = _riotApi.GetEventsAsync(_lastEventId is >= 0 ? _lastEventId : null, cancellationToken);
            await Task.WhenAll(playerTask, statsTask, eventsTask);
            player = await playerTask;
            gameStats = await statsTask;
            currentEvents = await eventsTask;
        }
        _detectedPlayer = player;
        var gameTime = GetDouble(gameStats.GetValueOrDefault("gameTime"), -1);

        var target = _windows.FindLeagueWindow();
        if (target == null || currentEvents.Any(e => e.GetValueOrDefault("EventName")?.ToString() == "GameEnd"))
        {
            if (_telemetryStarted && fullData != null) _telemetry.CaptureAll(now, fullData);
            if (_activePlayer != null)
                ProcessEvents(currentEvents, _activePlayer, now);
            await EndCaptureAsync();
            _review?.CompleteMatch();
            SetStatus(RecordingStatus.WaitingForGame, "等待英雄联盟对局",
                target == null ? "等待可用的 LOL 游戏窗口，未采集画面或音频" : "对局已结束，已停止采集", _warning);
            return;
        }

        var sessionReset = gameTime >= 0 && _lastGameTime >= 0 && gameTime + 5 < _lastGameTime;
        var playerChanged = _activePlayer == null || !SamePlayer(_activePlayer, player);
        if ((_captureTarget != null && (_captureTarget.ProcessId != target.ProcessId ||
            _captureTarget.WindowHandle != target.WindowHandle || _captureTarget.Rect != target.Rect)) || playerChanged || sessionReset)
            await EndCaptureAsync();
        if (sessionReset) _review?.CompleteMatch();
        _lastGameTime = gameTime;

        if (_captureTarget == null)
            await BeginCaptureAsync(target, cancellationToken);
        if (_apiUnavailable)
        {
            _apiUnavailable = false;
            SetStatus(RecordingStatus.Recording, $"正在录制：{player.GameName}", "仅采集游戏窗口，后台持续录制", _warning);
        }

        if (!_telemetryStarted)
        {
            StopTelemetryIfNeeded();
            var initialActivePlayer = fullData?.ActivePlayer ?? await _riotApi.GetActivePlayerDataAsync(cancellationToken);
            var initialPlayers = fullData?.Players ?? await _riotApi.GetPlayerListAsync(cancellationToken);
            player = player with { Team = ResolveTeam(player, initialPlayers) };
            _activePlayer = player;
            _lastEventId = null;
            _telemetry.SetPlayer(player);
            _telemetry.Start(_sessionDirectory!, now, gameStats, initialActivePlayer, initialPlayers);
            if (fullData != null) _telemetry.CaptureAll(now, fullData);
            _telemetryStarted = true;
            _nextTelemetryAt = now.AddSeconds(_config.TelemetryIntervalSeconds);

            // 新对局开始时 eventdata 会返回整局已经发生的事件。
            // 直接把游标推到最新，避免把开局前的旧击杀重新导出。
            var initialEvents = currentEvents;
            _lastEventId = initialEvents
                .Select(GetEventId)
                .Where(id => id >= 0)
                .DefaultIfEmpty(-1)
                .Max();
            foreach (var initialEvent in initialEvents) _telemetry.RecordEvent(now, initialEvent);

            SetStatus(RecordingStatus.Recording, $"正在录制：{player.GameName}",
                _config.RecordingMode == "full_game" ? "正在连续保存整局录像，不依赖事件触发" : "对局已连接，正在监听选定的高光事件", _warning);
        }

        // Retain the resolved team across polls; activeplayer does not include it.
        player = _activePlayer ?? player;
        ProcessEvents(currentEvents, player, now);

        if (now >= _nextTelemetryAt && _telemetryStarted)
        {
            if (fullData != null) _telemetry.CaptureAll(now, fullData);
            else
            {
                var activePlayer = await _riotApi.GetActivePlayerDataAsync(cancellationToken);
                var players = await _riotApi.GetPlayerListAsync(cancellationToken);
                _telemetry.Capture(now, gameStats, activePlayer, players);
            }
            _nextTelemetryAt = now.AddSeconds(_config.TelemetryIntervalSeconds);
        }
    }

    private void ProcessEvents(List<Dictionary<string, object>> events, PlayerIdentity player, DateTime now)
    {
        foreach (var evt in events.OrderBy(GetEventId))
        {
            var eventId = GetEventId(evt);
            if (_lastEventId.HasValue && eventId <= _lastEventId.Value)
                continue;
            if (eventId >= 0)
                _lastEventId = eventId;

            if (_telemetryStarted)
                _telemetry.RecordEvent(now, evt);

            var trigger = _evaluator.Evaluate(evt, player, now);
            if (trigger == null) continue;
            var fullGame = _config.RecordingMode == "full_game";
            if (!fullGame && !_config.RecordingEvents.Contains(trigger.Kind)) continue;
            if (!fullGame) _clipCoordinator.Trigger(trigger, player);
            if (_config.ShowEventNotifications)
            {
                // UI subscribers must never interrupt event detection or the encoded stream.
                foreach (EventHandler<HighlightDetectedEventArgs> handler in HighlightDetected?.GetInvocationList() ?? [])
                    try { handler(this, new(trigger, fullGame)); } catch { }
            }
        }

    }

    private async Task AdvanceExportAsync()
    {
        if (_config.RecordingMode == "full_game") return;
        if (_exportTask is { IsCompleted: false })
            return;
        await CompleteExportAsync();
        var now = _clock.NowSeconds;
        // Remuxing must not block API polling or detection of a foreground change.
        _exportTask = Task.Run(() => _clipCoordinator.TickAsync(now));
    }

    private async Task CompleteExportAsync()
    {
        var pending = _exportTask;
        _exportTask = null;
        if (pending == null) return;
        try
        {
            if (await pending != null) _exportWarning = null;
        }
        catch (Exception error)
        {
            _exportWarning = $"高光导出失败：{error.Message}";
            throw;
        }
    }

    private async Task<string?> FlushClipsAsync()
    {
        if (_config.RecordingMode == "full_game") return null;
        string? last = null;
        do
        {
            last = await _clipCoordinator.TickAsync(_clock.NowSeconds, force: true) ?? last;
        } while (_clipCoordinator.ActiveWindow != null);
        if (last != null)
        {
            _exportWarning = null;
            if (_warning?.StartsWith("高光导出失败") == true || _warning?.StartsWith("最后片段") == true)
                _warning = null;
        }
        return last;
    }

    private async Task BeginCaptureAsync(GameCaptureTarget target, CancellationToken cancellationToken)
    {
        _review?.BeginMatch();
        _warning = null;
        _ringBuffer.Clear();
        _clock.Start();
        var runtimeDirectory = _paths.ResolveRuntimeDirectory(_config.RuntimeDir);
        _sessionDirectory = Path.Combine(runtimeDirectory, $"session_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sessionDirectory);
        Directory.CreateDirectory(_paths.ResolveOutputDirectory(_config.OutputDir));
        SetStatus(RecordingStatus.Starting, "已检测到对局，正在启动采集", "正在初始化游戏窗口、编码器和音频设备");
        var audio = await StartAudioSourcesAsync(target.ProcessId);
        _capturingGameAudio = audio.GameAudio;
        _capturingMicrophone = audio.Microphone;
        if (_config.RecordingMode == "full_game")
            _fullGamePath = Path.Combine(_review?.OutputDirectory ?? _paths.ResolveOutputDirectory(_config.OutputDir),
                $"fullgame_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.mp4");
        await _pipeline.StartAsync(new CaptureOptions(
            target.WindowHandle, target.Rect, _config.Encoder, _config.EncoderPreset,
            _config.Fps, _config.Bitrate, audio.GameAudio, audio.Microphone,
            _config.SeparateAudioTracks, _config.AudioBitrate, _config.AudioOutputChannels,
            "window", 0, _fullGamePath == null ? null : _fullGamePath + ".part.mp4"), cancellationToken);
        _captureTarget = target;
        _captureStartedAt = _clock.NowSeconds;
    }

    private async Task StopCaptureInputsAsync()
    {
        try { _gameAudio.Stop(); } catch (Exception error) { _warning = error.Message; }
        try { _microphone.Stop(); } catch (Exception error) { _warning = error.Message; }
        _capturingGameAudio = false;
        _capturingMicrophone = false;
        try { await _pipeline.StopAsync(); } catch (Exception error) { _warning = error.Message; }
        _captureTarget = null;
        var fullGame = _fullGamePath;
        _fullGamePath = null;
        if (fullGame != null && File.Exists(fullGame + ".part.mp4"))
        {
            var pending = fullGame + ".part.mp4";
            try
            {
                if (_config.EmbedTelemetryFooter)
                {
                    _telemetry.Flush();
                    TelemetryArchiveResult? archive = null;
                    if (_telemetry.Path is { } path)
                    {
                        archive = await TelemetryArchive.AppendAsync(pending, new(path, _clock.StartedAtUtc, DateTime.UtcNow),
                            reserveBytes: incoming => new StorageQuotaManager().EnsurePendingAppendRoom(pending, fullGame,
                                _config.DiskQuotaGb, _config.MinimumFreeGb, incoming));
                    }
                    new MetadataFooterService().AppendMetadataFooter(pending, new Dictionary<string, object>
                    {
                        ["type"] = "full_game",
                        ["recording_started_at"] = _clock.StartedAtUtc.ToString("O"),
                        ["recorded_at"] = _clock.StartedAtUtc.ToString("O"),
                        ["capture_mode"] = _config.CaptureMode,
                        ["telemetry_interval_seconds"] = _config.TelemetryIntervalSeconds,
                        ["player"] = new Dictionary<string, string>
                        {
                            ["game_name"] = (_activePlayer ?? _detectedPlayer)?.GameName ?? "",
                            ["riot_id"] = (_activePlayer ?? _detectedPlayer)?.RiotId ?? "",
                            ["tag_line"] = (_activePlayer ?? _detectedPlayer)?.TagLine ?? ""
                        },
                        ["clip_window"] = new { start_seconds = 0, end_seconds = _clock.NowSeconds },
                        ["telemetry_archive"] = archive ?? new(0, 0, 0)
                    });
                }
            }
            catch (Exception error)
            {
                _warning = $"整局视频已保存，附加对局数据失败；原始数据保留在会话目录：{error.Message}";
            }
            try
            {
                File.Move(pending, fullGame);
                new StorageQuotaManager().Enforce(Path.GetDirectoryName(fullGame)!, _config.DiskQuotaGb, _config.MinimumFreeGb, fullGame);
            }
            catch (Exception error) { _warning = $"整局录像保留在 {pending}，完成保存失败：{error.Message}"; }
        }
    }

    private async Task EndCaptureAsync()
    {
        await StopCaptureInputsAsync();
        try
        {
            try { await CompleteExportAsync(); }
            catch (Exception error) { _warning = $"高光导出失败：{error.Message}"; }
            // Flush only the game frames already captured, before resetting the timeline.
            await FlushClipsAsync();
        }
        catch (Exception error)
        {
            _warning = $"最后片段未能保存：{error.Message}";
            _exportWarning = _warning;
            SetStatus(RecordingStatus.Error, "高光保存失败", _warning, _warning);
        }
        finally
        {
            _clipCoordinator.Reset();
            StopTelemetryIfNeeded();
            _ringBuffer.Clear();
            _clock.Stop();
            _activePlayer = null;
            _lastEventId = null;
            _lastGameTime = -1;
            _apiUnavailable = false;
        }
    }

    private async Task StopResourcesAsync()
    {
        _cts?.Cancel();
        if (_monitorTask != null)
        {
            try { await _monitorTask; } catch { }
            _monitorTask = null;
        }

        try { await StopCaptureInputsAsync(); } catch { }
        try { await CompleteExportAsync(); } catch { }
        _clipCoordinator.Reset();
        StopTelemetryIfNeeded();
        _ringBuffer.Clear();
        _clock.Stop();
        _cts?.Dispose();
        _cts = null;
        _activePlayer = null;
        _detectedPlayer = null;
        _lastEventId = null;
        _lastGameTime = -1;
    }

    private void AppendWarning(string message)
        => _warning = string.IsNullOrWhiteSpace(_warning) ? message : $"{_warning}；{message}";

    private void StopTelemetryIfNeeded()
    {
        if (!_telemetryStarted)
            return;
        try { _telemetry.Stop(); }
        catch (Exception error) { _warning = $"保存遥测失败：{error.Message}"; }
        finally { _telemetryStarted = false; }
    }

    private void UpdateBufferedStatus()
    {
        var current = Snapshot;
        if (current.Status is not (RecordingStatus.Recording or RecordingStatus.WaitingForGame or RecordingStatus.Paused or RecordingStatus.Retrying))
            return;

        lock (_stateLock)
        {
            _snapshot = current with
            {
                BufferedSeconds = _ringBuffer.BufferedSeconds,
                LastError = current.Status == RecordingStatus.Retrying ? current.LastError : ForDisplay(_exportWarning ?? _warning, 1500)
            };
        }
        StatusChanged?.Invoke(this, new RecordingStatusChangedEventArgs(Snapshot));
    }

    private void SetStatus(RecordingStatus status, string title, string? detail, string? error = null)
    {
        error = _exportWarning ?? error;
        var diagnosticError = error;
        var diagnosticDetail = detail;
        detail = ForDisplay(detail, 700);
        error = ForDisplay(error, 1500);
        bool changed;
        lock (_stateLock)
        {
            changed = _snapshot.Status != status || _snapshot.Title != title || _snapshot.Detail != (detail ?? string.Empty) || _snapshot.LastError != error;
            _snapshot = new RecordingSnapshot(
                status,
                title,
                detail ?? string.Empty,
                _ringBuffer.BufferedSeconds,
                error,
                _activePlayer ?? _detectedPlayer);
        }
        if (changed)
        {
            RuntimeDiagnostics.Write(_paths.ResolveRuntimeDirectory(_config.RuntimeDir),
                $"[{status}] {title} | {diagnosticDetail} | {diagnosticError}");
        }
        StatusChanged?.Invoke(this, new RecordingStatusChangedEventArgs(Snapshot));
    }

    private int MaxBufferSeconds()
        => _config.RecordingMode == "full_game" ? (int)Math.Ceiling(2 * _config.SegmentSeconds + 4) : _config.RetentionSeconds;

    private static string? ForDisplay(string? message, int limit)
        => message is { Length: var length } && length > limit ? message[..limit] + "…（完整原因见 listener.log）" : message;

    private static string ResolveTeam(PlayerIdentity player, List<Dictionary<string, object>> players)
    {
        foreach (var candidate in players)
        {
            var qualifiedId = candidate.GetValueOrDefault("riotId")?.ToString();
            var name = !string.IsNullOrWhiteSpace(qualifiedId) && qualifiedId.Contains('#')
                ? qualifiedId
                : candidate.GetValueOrDefault("summonerName")?.ToString() ??
                  candidate.GetValueOrDefault("riotIdGameName")?.ToString();
            if (player.Matches(name))
                return candidate.GetValueOrDefault("team")?.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static bool SamePlayer(PlayerIdentity left, PlayerIdentity right)
    {
        if (!string.IsNullOrWhiteSpace(left.RiotId) && !string.IsNullOrWhiteSpace(right.RiotId))
            return string.Equals(left.RiotId, right.RiotId, StringComparison.OrdinalIgnoreCase);
        return string.Equals(left.GameName, right.GameName, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatEncoder(string? encoder) => encoder switch
    {
        "h264_nvenc" => "NVIDIA NVENC",
        "h264_amf" => "AMD AMF",
        "h264_qsv" => "Intel QSV",
        "libx264" => "软件 x264",
        _ => "自动编码"
    };

    private static double GetDouble(object? value, double fallback)
    {
        if (value is double doubleValue) return doubleValue;
        if (value is float floatValue) return floatValue;
        if (value is int intValue) return intValue;
        if (value is long longValue) return longValue;
        return double.TryParse(value?.ToString(), out var parsed) ? parsed : fallback;
    }

    private static int GetEventId(Dictionary<string, object> evt)
    {
        var value = evt.GetValueOrDefault("EventID");
        if (value is int intValue) return intValue;
        if (value is long longValue && longValue <= int.MaxValue) return (int)longValue;
        return int.TryParse(value?.ToString(), out var parsed) ? parsed : -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _disposed = true;
        _lifecycle.Dispose();
    }
}
