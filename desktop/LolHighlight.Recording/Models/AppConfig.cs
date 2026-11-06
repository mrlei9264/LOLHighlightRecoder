using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LolHighlight.Recording.Models;

public sealed class AppConfig
{
    private static readonly Regex RatePattern = new(@"^\d+(\.\d+)?[kKmM]?$", RegexOptions.Compiled);

    [JsonPropertyName("ffmpeg_path")]
    public string FfmpegPath { get; set; } = "ffmpeg";

    [JsonPropertyName("output_dir")]
    public string OutputDir { get; set; } = "recordings";

    [JsonPropertyName("runtime_dir")]
    public string RuntimeDir { get; set; } = ".runtime";

    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 60;

    [JsonPropertyName("bitrate")]
    public string Bitrate { get; set; } = "20M";

    [JsonPropertyName("max_bitrate")]
    public string MaxBitrate { get; set; } = "25M";

    [JsonPropertyName("buffer_size")]
    public string BufferSize { get; set; } = "40M";

    [JsonPropertyName("encoder")]
    public string Encoder { get; set; } = "auto";

    [JsonPropertyName("encoder_preset")]
    public string EncoderPreset { get; set; } = "p1";

    [JsonPropertyName("capture_mode")]
    public string CaptureMode { get => _captureMode; set => _captureMode = value == "display" ? "window" : value; }
    private string _captureMode = "window";

    [JsonPropertyName("review_after_match")]
    public bool ReviewAfterMatch { get; set; } = true;

    [JsonPropertyName("recording_mode")]
    public string RecordingMode { get; set; } = "highlights";

    [JsonPropertyName("recording_events")]
    public string[] RecordingEvents { get; set; } = RecordingEventCatalog.Defaults();

    [JsonPropertyName("show_event_notifications")]
    public bool ShowEventNotifications { get; set; } = true;

    [JsonPropertyName("event_notification_style")]
    public string EventNotificationStyle { get; set; } = EventNotificationStyles.Default;

    [JsonPropertyName("disk_quota_gb")]
    public int DiskQuotaGb { get; set; } = 50;

    [JsonPropertyName("minimum_free_gb")]
    public int MinimumFreeGb { get; set; } = 5;

    [JsonPropertyName("audio_enabled")]
    public bool AudioEnabled { get; set; } = true;

    [JsonPropertyName("microphone_enabled")]
    public bool MicrophoneEnabled { get; set; } = false;

    [JsonPropertyName("separate_audio_tracks")]
    public bool SeparateAudioTracks { get; set; } = true;

    [JsonPropertyName("audio_bitrate")]
    public string AudioBitrate { get; set; } = "192k";

    [JsonPropertyName("audio_output_channels")]
    public int AudioOutputChannels { get; set; } = 2;

    [JsonPropertyName("pre_roll_seconds")]
    public double PreRollSeconds { get; set; } = 45.0;

    [JsonPropertyName("post_roll_seconds")]
    public double PostRollSeconds { get; set; } = 15.0;

    [JsonPropertyName("poll_interval_seconds")]
    public double PollIntervalSeconds { get; set; } = 0.1;

    [JsonPropertyName("telemetry_interval_seconds")]
    public double TelemetryIntervalSeconds { get; set; } = 0.1;

    [JsonPropertyName("embed_telemetry_footer")]
    public bool EmbedTelemetryFooter { get; set; } = true;

    [JsonPropertyName("segment_seconds")]
    public double SegmentSeconds { get; set; } = 1.0;

    [JsonPropertyName("segment_grace_seconds")]
    public double SegmentGraceSeconds { get; set; } = 2.0;

    [JsonPropertyName("memory_buffer_mb")]
    public int MemoryBufferMb { get; set; } = 512;

    [JsonPropertyName("audio_capture_mode")]
    public string AudioCaptureMode { get; set; } = "process_loopback";

    public AppConfig Clone()
    {
        var copy = (AppConfig)MemberwiseClone();
        copy.RecordingEvents = RecordingEvents?.ToArray() ?? RecordingEventCatalog.Defaults();
        return copy;
    }

    [JsonIgnore]
    public int RetentionSeconds => (int)Math.Ceiling(PreRollSeconds + PostRollSeconds + SegmentGraceSeconds + 2 * SegmentSeconds + 5);

    [JsonIgnore]
    public double MaxClipSeconds => Math.Max(2, PreRollSeconds + PostRollSeconds);

    [JsonIgnore]
    public double EstimatedBufferMb
    {
        get
        {
            var value = MaxBitrate?.Trim() ?? "";
            var suffix = value.Length > 0 ? char.ToUpperInvariant(value[^1]) : '\0';
            if (!double.TryParse(value.TrimEnd('k', 'K', 'm', 'M'),
                    System.Globalization.NumberStyles.AllowDecimalPoint,
                    System.Globalization.CultureInfo.InvariantCulture, out var bits)) return 0;
            bits *= suffix == 'M' ? 1_000_000 : suffix == 'K' ? 1000 : 1;
            return bits * RetentionSeconds / 8 / (1024 * 1024);
        }
    }

    public void Validate()
    {
        foreach (var seconds in new[] { PreRollSeconds, PostRollSeconds, PollIntervalSeconds,
                     TelemetryIntervalSeconds, SegmentSeconds, SegmentGraceSeconds })
            if (!double.IsFinite(seconds))
                throw new ArgumentException("时间配置必须是有限数值");

        if (Fps <= 0 || Fps > 480)
            throw new ArgumentException("帧率必须大于 0 且不超过 480");

        if (PreRollSeconds < 0 || PostRollSeconds < 0)
            throw new ArgumentException("回溯和延后秒数不能为负数");

        if (PreRollSeconds > 600 || PostRollSeconds > 600)
            throw new ArgumentException("回溯和延后秒数不能超过 600 秒");

        if (RecordingMode == "highlights" && PreRollSeconds + PostRollSeconds <= 0)
            throw new ArgumentException("高光片段时长必须大于 0");

        if (string.IsNullOrWhiteSpace(Encoder) || !new[] { "auto", "nvenc", "amf", "qsv", "x264",
                "h264_nvenc", "h264_amf", "h264_qsv", "libx264" }.Contains(Encoder.Trim().ToLowerInvariant()))
            throw new ArgumentException("编码器无效，请选择 auto、nvenc、amf、qsv 或 x264");

        if (string.IsNullOrWhiteSpace(EncoderPreset))
            throw new ArgumentException("编码预设不能为空");

        if (string.IsNullOrWhiteSpace(OutputDir) || string.IsNullOrWhiteSpace(RuntimeDir))
            throw new ArgumentException("输出目录和运行目录不能为空");
        _ = Path.GetFullPath(OutputDir);
        _ = Path.GetFullPath(RuntimeDir);

        if (!IsRate(Bitrate))
            throw new ArgumentException("视频码率格式无效，请使用 20M、8000k 这样的格式");

        if (!IsRate(MaxBitrate))
            throw new ArgumentException("最大码率格式无效");

        if (!IsRate(BufferSize))
            throw new ArgumentException("码率缓冲格式无效");

        if (!IsRate(AudioBitrate))
            throw new ArgumentException("音频码率格式无效");

        if (PollIntervalSeconds < 0.05 || PollIntervalSeconds > 60)
            throw new ArgumentException("轮询间隔必须在 0.05～60 秒之间");

        if (TelemetryIntervalSeconds < 0.1 || TelemetryIntervalSeconds > 60)
            throw new ArgumentException("遥测间隔必须在 0.1～60 秒之间");

        if (RecordingMode is not ("highlights" or "full_game"))
            throw new ArgumentException("请选择事件高光或整局录制模式");
        if (!EventNotificationStyles.IsSupported(EventNotificationStyle))
            throw new ArgumentException("事件提示样式无效，请在设置中选择一种样式");
        if (RecordingEvents == null || RecordingEvents.Any(id => !RecordingEventCatalog.All.Any(kind => kind.Id == id)))
            throw new ArgumentException("录制事件设定无效");

        if (AudioOutputChannels != 1 && AudioOutputChannels != 2)
            throw new ArgumentException("audio_output_channels must be 1 or 2");

        if (CaptureMode != "window" && CaptureMode != "display")
            throw new ArgumentException("仅支持 LOL 游戏窗口采集");

        if (DiskQuotaGb <= 0 || DiskQuotaGb > 4096 || MinimumFreeGb < 1 || MinimumFreeGb > 4096)
            throw new ArgumentException("disk quota must be positive and minimum_free_gb must be at least 1");

        if (SegmentSeconds < 0.1 || SegmentSeconds > 5)
            throw new ArgumentException("内存分块时长必须在 0.1～5 秒之间");

        if (SegmentGraceSeconds < 0 || SegmentGraceSeconds > 60)
            throw new ArgumentException("导出宽限时间必须在 0～60 秒之间");

        if (MemoryBufferMb < 64 || MemoryBufferMb > 8192)
            throw new ArgumentException("内存缓冲区必须在 64～8192 MB 之间");

        if (string.IsNullOrWhiteSpace(FfmpegPath))
            throw new ArgumentException("ffmpeg_path must not be empty");

        if (AudioCaptureMode != "system_mix" && AudioCaptureMode != "process_loopback")
            throw new ArgumentException("audio_capture_mode must be 'process_loopback'");
    }

    private static bool IsRate(string value)
        => !string.IsNullOrWhiteSpace(value) && RatePattern.IsMatch(value.Trim()) &&
           double.TryParse(value.Trim().TrimEnd('k', 'K', 'm', 'M'),
               System.Globalization.NumberStyles.AllowDecimalPoint,
               System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0;
}
