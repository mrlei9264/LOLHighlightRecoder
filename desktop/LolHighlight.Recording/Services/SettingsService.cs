using System.Text.Json;
using LolHighlight.Recording.Models;

namespace LolHighlight.Recording.Services;

public interface ISettingsService
{
    AppConfig Load();
    void Save(AppConfig settings);
}

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configPath;
    private readonly AppConfig _liveConfig;
    private readonly Func<bool>? _isRecording;
    private readonly LolHighlight.Recording.Infrastructure.AppPaths? _paths;

    public SettingsService(string configPath, AppConfig liveConfig, Func<bool>? isRecording = null,
        LolHighlight.Recording.Infrastructure.AppPaths? paths = null)
    {
        _configPath = configPath;
        _liveConfig = liveConfig;
        _isRecording = isRecording;
        _paths = paths;
    }

    public AppConfig Load()
    {
        if (!File.Exists(_configPath))
            return Clone(_liveConfig);

        try
        {
            var json = File.ReadAllText(_configPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            loaded.Validate();
            _paths?.NormalizeDataDirectories(loaded);
            return loaded;
        }
        catch
        {
            return Clone(_liveConfig);
        }
    }

    public void Save(AppConfig settings)
    {
        if (_isRecording?.Invoke() == true)
            throw new InvalidOperationException("请先停止监听，再保存设置，以免改变正在录制的片段参数。");
        settings.Validate();
        if (_paths != null && !_paths.IsInsideProgramDirectory(settings.RuntimeDir))
            throw new ArgumentException("缓存目录必须位于应用数据目录内，请使用 .runtime 等相对路径。");
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = _configPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions) + Environment.NewLine;
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _configPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch (IOException) { }
        }

        CopyInto(settings, _liveConfig);
    }

    private static AppConfig Clone(AppConfig source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }

    private static void CopyInto(AppConfig source, AppConfig target)
    {
        target.FfmpegPath = source.FfmpegPath;
        target.OutputDir = source.OutputDir;
        target.RuntimeDir = source.RuntimeDir;
        target.Fps = source.Fps;
        target.Bitrate = source.Bitrate;
        target.MaxBitrate = source.MaxBitrate;
        target.BufferSize = source.BufferSize;
        target.Encoder = source.Encoder;
        target.EncoderPreset = source.EncoderPreset;
        target.CaptureMode = source.CaptureMode;
        target.RecordingMode = source.RecordingMode;
        target.ReviewAfterMatch = source.ReviewAfterMatch;
        target.RecordingEvents = source.RecordingEvents.ToArray();
        target.ShowEventNotifications = source.ShowEventNotifications;
        target.EventNotificationStyle = source.EventNotificationStyle;
        target.DiskQuotaGb = source.DiskQuotaGb;
        target.MinimumFreeGb = source.MinimumFreeGb;
        target.AudioEnabled = source.AudioEnabled;
        target.MicrophoneEnabled = source.MicrophoneEnabled;
        target.SeparateAudioTracks = source.SeparateAudioTracks;
        target.AudioBitrate = source.AudioBitrate;
        target.AudioOutputChannels = source.AudioOutputChannels;
        target.PreRollSeconds = source.PreRollSeconds;
        target.PostRollSeconds = source.PostRollSeconds;
        target.PollIntervalSeconds = source.PollIntervalSeconds;
        target.TelemetryIntervalSeconds = source.TelemetryIntervalSeconds;
        target.EmbedTelemetryFooter = source.EmbedTelemetryFooter;
        target.SegmentSeconds = source.SegmentSeconds;
        target.SegmentGraceSeconds = source.SegmentGraceSeconds;
        target.MemoryBufferMb = source.MemoryBufferMb;
        target.AudioCaptureMode = "process_loopback";
    }
}
