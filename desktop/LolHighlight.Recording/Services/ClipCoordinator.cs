using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;

namespace LolHighlight.Recording.Services;

public interface IClipCoordinator
{
    ClipWindow? ActiveWindow { get; }
    string? LastExportWarning => null;
    void Trigger(HighlightTrigger item, PlayerIdentity player);
    Task<string?> TickAsync(double nowSeconds, bool force = false, CancellationToken cancellationToken = default);
    void Reset();
}

public sealed class ClipCoordinator : IClipCoordinator
{
    private readonly AppConfig _config;
    private readonly IExportManager _exportManager;
    private readonly IRecordingClock _clock;
    private readonly AppPaths _paths;
    private readonly ITelemetryCollector? _telemetry;
    private readonly MatchReviewStore? _review;
    private readonly object _lock = new();

    private ClipWindow? _activeWindow;
    private double _retryAfterSeconds = double.MinValue;
    private ClipWindow? _retryWindow;
    private bool _exporting;
    private double _committedEnd;
    private double _reservedEnd;
    private readonly HashSet<int> _seenEvents = new();
    public string? LastExportWarning { get; private set; }

    public ClipWindow? ActiveWindow
    {
        get { lock (_lock) return (_retryWindow ?? _activeWindow)?.Snapshot(); }
    }

    public ClipCoordinator(AppConfig config, IExportManager exportManager, IRecordingClock clock, AppPaths paths,
        ITelemetryCollector? telemetry = null, MatchReviewStore? review = null)
    {
        _config = config;
        _exportManager = exportManager;
        _clock = clock;
        _paths = paths;
        _telemetry = telemetry;
        _review = review;
    }

    public void Trigger(HighlightTrigger item, PlayerIdentity player)
    {
        var eventTime = TriggerTime(item);
        var start = Math.Max(0, eventTime - _config.PreRollSeconds);
        var end = eventTime + PostRoll;

        lock (_lock)
        {
            if (!_seenEvents.Add(item.EventId))
                return;
            start = Math.Max(start, Math.Max(_committedEnd, _reservedEnd));
            if (end <= start) return;
            if (_activeWindow == null)
            {
                _activeWindow = new ClipWindow
                {
                    StartedAt = start,
                    EndsAt = end,
                    Player = player,
                    Triggers = new List<HighlightTrigger> { item }
                };
                return;
            }

            if (!_activeWindow.IsContinuation)
                _activeWindow.StartedAt = Math.Min(_activeWindow.StartedAt, start);
            _activeWindow.EndsAt = Math.Max(_activeWindow.EndsAt, end);

            if (!_activeWindow.Triggers.Any(trigger => trigger.EventId == item.EventId))
                _activeWindow.Triggers.Add(item);
        }
    }

    public async Task<string?> TickAsync(double nowSeconds, bool force = false, CancellationToken cancellationToken = default)
    {
        ClipWindow? window;
        lock (_lock)
        {
            if (_exporting)
                return null;
            window = _retryWindow ?? _activeWindow;
            if (window == null)
                return null;

            var exportEnd = Math.Min(window.EndsAt, window.StartedAt + _config.MaxClipSeconds);
            if (!force && nowSeconds < exportEnd + _config.SegmentGraceSeconds)
                return null;

            if (!force && nowSeconds < _retryAfterSeconds)
                return null;

            _exporting = true;
            if (_retryWindow != null)
                _retryWindow = null;
            else if (!force && exportEnd < window.EndsAt)
            {
                // Save long fights in bounded parts before their beginning is evicted.
                _activeWindow = window.Snapshot();
                _activeWindow.StartedAt = exportEnd;
                _activeWindow.IsContinuation = true;
                _activeWindow.Triggers = _activeWindow.Triggers.Where(trigger =>
                    TriggerTime(trigger) + PostRoll >= _activeWindow.StartedAt).ToList();
            }
            else
                _activeWindow = null;
            window = window.Snapshot();
            window.EndsAt = force ? Math.Max(window.StartedAt + 0.001, Math.Min(window.EndsAt, nowSeconds)) : exportEnd;
            _reservedEnd = window.EndsAt;
        }

        try
        {
            var result = await ExportClipAsync(window, cancellationToken);
            lock (_lock)
            {
                _committedEnd = Math.Max(_committedEnd, _exportManager.LastExportedEnd ?? window.EndsAt);
                if (_activeWindow != null)
                {
                    _activeWindow.StartedAt = Math.Max(_activeWindow.StartedAt, _committedEnd);
                    if (_activeWindow.EndsAt <= _activeWindow.StartedAt) _activeWindow = null;
                }
                _retryAfterSeconds = double.MinValue;
            }
            return result;
        }
        catch
        {
            lock (_lock)
            {
                // New triggers may already be forming the next clip while export runs.
                _retryWindow = window;
                // 导出失败后等待几秒再重试，避免每 200ms 拉起一个 FFmpeg 进程。
                _retryAfterSeconds = nowSeconds + 5;
            }
            throw;
        }
        finally
        {
            lock (_lock) { _exporting = false; _reservedEnd = 0; }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _activeWindow = null;
            _retryWindow = null;
            _retryAfterSeconds = double.MinValue;
            _committedEnd = 0;
            _reservedEnd = 0;
            _seenEvents.Clear();
        }
    }

    private double PostRoll => Math.Max(_config.PostRollSeconds, 1d / Math.Max(1, _config.Fps));

    private double TriggerTime(HighlightTrigger trigger) => trigger.RecordingSeconds ??
        (_clock.IsRunning ? _clock.ToSessionSeconds(trigger.DetectedAt) : Math.Max(0, trigger.EventTime));

    private async Task<string> ExportClipAsync(ClipWindow window, CancellationToken cancellationToken)
    {
        var outputDirectory = _review?.OutputDirectory ?? _paths.ResolveOutputDirectory(_config.OutputDir);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(
            outputDirectory,
            $"highlight_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..8]}.mp4");

        var metadata = new Dictionary<string, object>
        {
            ["type"] = "highlight",
            ["continuation"] = window.IsContinuation,
            ["recorded_at"] = DateTime.UtcNow.ToString("O"),
            ["recording_started_at"] = _clock.StartedAtUtc.ToString("O"),
            [VideoChapters.ExportMetadataKey] = window.Triggers
                .Select(trigger => new RecordingChapter(TriggerTime(trigger), trigger.Kind)).ToArray(),
            ["triggers"] = window.Triggers.Select(trigger => new Dictionary<string, object>
            {
                ["event_id"] = trigger.EventId,
                ["kind"] = trigger.Kind,
                ["label"] = trigger.Label,
                ["event_time"] = trigger.EventTime,
                ["session_seconds"] = TriggerTime(trigger),
                ["detected_at"] = trigger.DetectedAt.ToUniversalTime().ToString("O")
            }).ToList(),
            ["player"] = new Dictionary<string, string>
            {
                ["riot_id"] = window.Player.RiotId,
                ["game_name"] = window.Player.GameName,
                ["tag_line"] = window.Player.TagLine
            },
            ["clip_window"] = new Dictionary<string, double>
            {
                ["start_seconds"] = window.StartedAt,
                ["end_seconds"] = window.EndsAt
            }
        };

        string? telemetryWarning = null;
        if (_config.EmbedTelemetryFooter && _telemetry?.Path is { } telemetryPath)
        {
            try
            {
                _telemetry.Flush();
                var start = _clock.StartedAtUtc.AddSeconds(window.StartedAt);
                var end = _clock.StartedAtUtc.AddSeconds(window.EndsAt);
                var telemetry = await Task.Run(() => TelemetryReader.ReadTelemetryWindow(telemetryPath, start, end,
                    cancellationToken), cancellationToken);
                metadata["telemetry"] = telemetry.Records;
                metadata["telemetry_truncated"] = telemetry.Truncated;
                metadata["_telemetry_archive"] = new TelemetryArchiveRequest(telemetryPath, start, end);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                telemetryWarning = $"高光录像已保存，无法读取原始对局数据：{error.Message}";
            }
        }

        var result = await _exportManager.ExportAsync(
            window.StartedAt,
            window.EndsAt,
            outputPath,
            metadata,
            cancellationToken);
        LastExportWarning = _exportManager.LastExportWarning ?? telemetryWarning;
        return result;
    }
}
