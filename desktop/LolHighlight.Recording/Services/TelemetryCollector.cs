using System.Text.Json;
using LolHighlight.Recording.Models;

namespace LolHighlight.Recording.Services;

public interface ITelemetryCollector
{
    string? Path { get; }
    void SetPlayer(PlayerIdentity player);
    void Start(string sessionDir, DateTime capturedAt, Dictionary<string, object> gameStats, Dictionary<string, object> activePlayer, List<Dictionary<string, object>> players);
    void Capture(DateTime capturedAt, Dictionary<string, object> gameStats, Dictionary<string, object> activePlayer, List<Dictionary<string, object>> players);
    void CaptureAll(DateTime capturedAt, LiveGameData data)
        => Capture(capturedAt, data.Game, data.ActivePlayer, data.Players);
    void RecordEvent(DateTime capturedAt, Dictionary<string, object> evt);
    void Stop();
    void Flush();
}

public sealed class TelemetryCollector : ITelemetryCollector, IDisposable
{
    private PlayerIdentity _player;
    private StreamWriter? _writer;
    private double? _lastHealth;
    private readonly object _writerLock = new();
    private long _nextDiskCheck;

    public TelemetryCollector(PlayerIdentity player)
    {
        _player = player;
    }

    public string? Path { get; private set; }

    public void SetPlayer(PlayerIdentity player) => _player = player;

    public void Start(string sessionDir, DateTime capturedAt, Dictionary<string, object> gameStats, Dictionary<string, object> activePlayer, List<Dictionary<string, object>> players)
    {
        Stop();
        Directory.CreateDirectory(sessionDir);
        _lastHealth = null;
        Path = System.IO.Path.Combine(sessionDir, "telemetry.ndjson");
        lock (_writerLock)
        {
            _writer = new StreamWriter(Path, append: true, encoding: new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
            _nextDiskCheck = 0;
        }

        WriteRecord(new Dictionary<string, object>
        {
            ["type"] = "session",
            ["captured_at"] = UtcIso(capturedAt),
            ["player"] = new Dictionary<string, string>
            {
                ["riot_id"] = _player.RiotId,
                ["game_name"] = _player.GameName,
                ["tag_line"] = _player.TagLine
            },
            ["game"] = gameStats,
            ["active_player"] = activePlayer,
            ["players"] = players
        });

        Capture(capturedAt, gameStats, activePlayer, players);
    }

    public void Capture(DateTime capturedAt, Dictionary<string, object> gameStats, Dictionary<string, object> activePlayer, List<Dictionary<string, object>> players)
    {
        var championStats = activePlayer.GetValueOrDefault("championStats") as Dictionary<string, object>
            ?? new Dictionary<string, object>();
        var healthObj = championStats.GetValueOrDefault("currentHealth");

        var derived = new Dictionary<string, double>();
        var health = GetDouble(healthObj);
        if (health.HasValue && _lastHealth.HasValue)
        {
            var delta = health.Value - _lastHealth.Value;
            derived["net_health_change"] = Math.Round(delta, 3);
            if (delta < 0)
                derived["estimated_net_damage_received"] = Math.Round(-delta, 3);
            else if (delta > 0)
                derived["estimated_net_healing"] = Math.Round(delta, 3);
        }

        if (health.HasValue)
            _lastHealth = health.Value;

        WriteRecord(new Dictionary<string, object>
        {
            ["type"] = "snapshot",
            ["captured_at"] = UtcIso(capturedAt),
            ["game"] = gameStats,
            ["active_player"] = activePlayer,
            ["players"] = players,
            ["derived"] = derived
        });
    }

    public void CaptureAll(DateTime capturedAt, LiveGameData data)
    {
        // All original fields are preserved. Historical events are stored once in the event stream.
        var raw = data.Raw.Where(pair => pair.Key != "events").ToDictionary(pair => pair.Key, pair => pair.Value);
        if (data.Raw.GetValueOrDefault("events") is Dictionary<string, object> events)
        {
            var context = events.Where(pair => pair.Key != "Events").ToDictionary(pair => pair.Key, pair => pair.Value);
            if (context.Count > 0) raw["events"] = context;
        }
        WriteRecord(new Dictionary<string, object>
        {
            ["type"] = "snapshot",
            ["captured_at"] = UtcIso(capturedAt),
            ["data"] = raw
        });
    }

    public void RecordEvent(DateTime capturedAt, Dictionary<string, object> evt)
    {
        WriteRecord(new Dictionary<string, object>
        {
            ["type"] = "event",
            ["captured_at"] = UtcIso(capturedAt),
            ["event"] = evt
        });
    }

    public void Stop()
    {
        lock (_writerLock)
        {
            var writer = _writer;
            _writer = null;
            writer?.Dispose();
        }
    }

    public void Flush()
    {
        lock (_writerLock) _writer?.Flush();
    }

    private void WriteRecord(Dictionary<string, object> record)
    {
        var json = JsonSerializer.Serialize(record);
        lock (_writerLock)
        {
            if (_writer == null) return;
            if (_writer.BaseStream.Position >= _nextDiskCheck)
            {
                var drive = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(Path!))!);
                if (drive.AvailableFreeSpace < 256L * 1024 * 1024)
                    throw new IOException("会话数据所在磁盘剩余空间不足 256 MB，已停止采集以保留现有数据");
                _nextDiskCheck = _writer.BaseStream.Position + 16 * 1024 * 1024;
            }
            // Keep the session spool until embedding is complete; rotating it would lose early match data.
            _writer.WriteLine(json);
        }
    }

    public void Dispose() => Stop();

    private static string UtcIso(DateTime value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private static double? GetDouble(object? value)
    {
        if (value is double doubleValue) return doubleValue;
        if (value is float floatValue) return floatValue;
        if (value is int intValue) return intValue;
        if (value is long longValue) return longValue;
        return double.TryParse(value?.ToString(), out var parsed) ? parsed : null;
    }
}
