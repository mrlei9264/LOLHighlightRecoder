using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using LolHighlight.Desktop.Services;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly AppConfig _config;
    private readonly AppPaths? _paths;
    private readonly ISettingsPickerService? _pickers;
    private readonly IRecordingSessionService? _session;
    private readonly DispatcherQueue? _dispatcher;
    private string? _savedDraft;
    private string? _lastCustomFfmpeg;
    private bool _isPickingPath;
    private bool _disposed;
    private bool _wasListening;
    private string _recordingMode = "highlights";
    private bool _reviewAfterMatch = true;
    private bool _deleteToRecycleBin = true;
    private bool _showEventNotifications = true;
    private string _eventNotificationStyle = EventNotificationStyles.Default;
    private double _telemetryInterval = 0.1;
    private double _pollInterval = 0.1;

    private string _captureMode = "window";
    private string _encoder = "auto";
    private int _fps = 60;
    private string _bitrate = "20M";
    private string _maxBitrate = "25M";
    private string _bufferSize = "40M";
    private double _preRoll = 45;
    private double _postRoll = 15;
    private double _segmentGrace = 2;
    private int _diskQuotaGb = 50;
    private int _minimumFreeGb = 5;
    private int _memoryBufferMb = 512;
    private bool _audioEnabled = true;
    private bool _microphoneEnabled;
    private bool _separateAudioTracks = true;
    private bool _embedTelemetryFooter = true;
    private string _ffmpegPath = "ffmpeg";
    private string _outputDir = "recordings";
    private string _saveMessage = "";
    private bool _saveFailed;

    public SettingsViewModel(ISettingsService settingsService, AppConfig config, AppPaths? paths = null,
        ISettingsPickerService? pickers = null, IRecordingSessionService? session = null)
    {
        _settingsService = settingsService;
        _config = config;
        _paths = paths;
        _pickers = pickers;
        _session = session;
        try { _dispatcher = DispatcherQueue.GetForCurrentThread(); } catch { }

        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(ResetToDefaults);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        SelectOutputFolderCommand = new AsyncRelayCommand(() => PickPathAsync(false));
        SelectFfmpegCommand = new AsyncRelayCommand(() => PickPathAsync(true));
        foreach (var choice in RecordingEvents) choice.PropertyChanged += EventSelectionChanged;

        LoadSettings();
        _savedDraft = DraftFingerprint();
        _wasListening = IsListening;
        if (_session != null) _session.StatusChanged += SessionStatusChanged;
    }

    public IReadOnlyList<RecordingEventChoice> RecordingEvents { get; } = RecordingEventCatalog.All
        .Select(kind => new RecordingEventChoice(kind.Id, kind.Label)).ToArray();
    public string RecordingMode { get => _recordingMode; set => SetField(ref _recordingMode, value); }
    public bool ReviewAfterMatch { get => _reviewAfterMatch; set => SetField(ref _reviewAfterMatch, value); }
    public bool DeleteToRecycleBin { get => _deleteToRecycleBin; set => SetField(ref _deleteToRecycleBin, value); }
    public bool IsHighlightMode => RecordingMode == "highlights";
    public bool ShowEventNotifications { get => _showEventNotifications; set => SetField(ref _showEventNotifications, value); }
    public IReadOnlyList<EventNotificationStyleChoice> EventNotificationStyleOptions => EventNotificationStyles.All;
    public string EventNotificationStyle
    {
        get => _eventNotificationStyle;
        set
        {
            if (EventNotificationStyles.IsSupported(value) && SetField(ref _eventNotificationStyle, value))
                OnPropertyChanged(nameof(EventNotificationStyleDescription));
        }
    }
    public string EventNotificationStyleDescription => EventNotificationStyles.All
        .First(style => style.Id == EventNotificationStyle).Description;
    public double TelemetryInterval
    {
        get => _telemetryInterval;
        set
        {
            if (!double.IsFinite(value) || value < 0.1 || value > 60) return;
            _pollInterval = value;
            SetField(ref _telemetryInterval, value);
            NotifySaveState();
        }
    }
    public double TelemetryIntervalMilliseconds
    {
        get => TelemetryInterval * 1000;
        set
        {
            if (double.IsFinite(value)) TelemetryInterval = value / 1000;
        }
    }
    public string CaptureModeHint => "仅录制游戏；最小化且无新画面时保留最后一帧。";

    public IReadOnlyList<SettingsChoice<bool>> AudioTrackOptions { get; } = new[]
    {
        new SettingsChoice<bool>(true, "独立音轨 · 保留游戏与麦克风"),
        new SettingsChoice<bool>(false, "混合音轨 · 方便直接播放")
    };
    public string CaptureMode { get => _captureMode; set => SetField(ref _captureMode, value == "display" ? "window" : value); }
    public string Encoder { get => _encoder; set => SetField(ref _encoder, value); }
    public string Bitrate { get => _bitrate; set => SetField(ref _bitrate, value); }
    public string MaxBitrate { get => _maxBitrate; set => SetField(ref _maxBitrate, value); }
    public string BufferSize { get => _bufferSize; set => SetField(ref _bufferSize, value); }
    public string FfmpegPath
    {
        get => _ffmpegPath;
        set
        {
            if (!SetField(ref _ffmpegPath, value)) return;
            if (UseCustomFfmpeg && !string.IsNullOrWhiteSpace(value)) _lastCustomFfmpeg = value;
            OnPropertyChanged(nameof(UseCustomFfmpeg));
        }
    }

    public bool UseCustomFfmpeg
    {
        get => !FfmpegPath.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) &&
               !FfmpegPath.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value == UseCustomFfmpeg) return;
            FfmpegPath = value ? _lastCustomFfmpeg ?? "" : "ffmpeg";
        }
    }

    public double VideoBitrateMbps
    {
        get => RateMbps(Bitrate);
        set
        {
            if (!double.IsFinite(value) || value <= 0 || Math.Abs(value - VideoBitrateMbps) < 0.0000001) return;
            Bitrate = FormatMbps(value);
            if (MaxBitrateMbps < value) MaxBitrate = FormatMbps(value);
        }
    }
    public double MaxBitrateMbps
    {
        get => RateMbps(MaxBitrate);
        set
        {
            if (!double.IsFinite(value) || value <= 0 || Math.Abs(value - MaxBitrateMbps) < 0.0000001) return;
            MaxBitrate = FormatMbps(Math.Max(VideoBitrateMbps, value));
        }
    }
    public double RateBufferMegabits
    {
        get => RateMbps(BufferSize);
        set
        {
            if (!double.IsFinite(value) || value <= 0 || Math.Abs(value - RateBufferMegabits) < 0.0000001) return;
            BufferSize = FormatMbps(value);
        }
    }
    public string SizeEstimate => $"仅视频约 {VideoBitrateMbps * 60 / 8:0.#} MB / 分钟，实际大小随内容变化。";
    public bool CanSeparateAudioTracks => AudioEnabled && MicrophoneEnabled;
    public bool IsPickingPath { get => _isPickingPath; private set => SetField(ref _isPickingPath, value); }
    public bool CanEditPaths => !IsPickingPath;
    public bool IsListening => _session?.IsRunning == true;
    public bool HasUnsavedChanges => _savedDraft != null && DraftFingerprint() != _savedDraft;
    public bool CanSave => HasUnsavedChanges && !IsPickingPath && !IsListening &&
        (!UseCustomFfmpeg || !string.IsNullOrWhiteSpace(FfmpegPath)) && (!IsHighlightMode || PreRoll + PostRoll > 0) &&
        (!IsHighlightMode || RecordingEvents.Any(choice => choice.IsSelected));
    public string SettingsStateText => IsListening ? "正在录制监听，更改将在停止后自动保存" :
        UseCustomFfmpeg && string.IsNullOrWhiteSpace(FfmpegPath) ? "请选择 FFmpeg 程序，或关闭自定义选项" :
        IsHighlightMode && PreRoll + PostRoll <= 0 ? "片段总时长必须大于 0 秒" :
        IsHighlightMode && !RecordingEvents.Any(choice => choice.IsSelected) ? "请至少选择一种录制事件" :
        HasUnsavedChanges ? "更改待自动保存，请检查参数是否有效" : "所有更改已自动保存";

    public string OutputDir
    {
        get => _outputDir;
        set
        {
            if (SetField(ref _outputDir, value))
                OnPropertyChanged(nameof(OutputPathPreview));
        }
    }

    public string OutputPathPreview => _paths == null
        ? OutputDir
        : _paths.ResolveOutputDirectory(OutputDir);

    public int Fps
    {
        get => _fps;
        set
        {
            if (value < 1 || value > 480) return;
            if (!SetField(ref _fps, value))
                return;
            OnPropertyChanged(nameof(FpsValue));
        }
    }

    // 滑块使用 double，持久化配置保持整数。
    public double FpsValue
    {
        get => Fps;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;
            Fps = (int)Math.Clamp(Math.Round(value), 1, 480);
        }
    }

    public double PreRoll
    {
        get => _preRoll;
        set => SetField(ref _preRoll, double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 600));
    }

    public double PostRoll
    {
        get => _postRoll;
        set => SetField(ref _postRoll, double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 600));
    }

    public double SegmentGrace
    {
        get => _segmentGrace;
        set => SetField(ref _segmentGrace, double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 60));
    }

    public int DiskQuotaGb
    {
        get => _diskQuotaGb;
        set
        {
            if (SetField(ref _diskQuotaGb, value))
                OnPropertyChanged(nameof(DiskQuotaValue));
        }
    }

    public double DiskQuotaValue
    {
        get => DiskQuotaGb;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;
            DiskQuotaGb = (int)Math.Clamp(Math.Round(value), 1, 4096);
        }
    }

    public int MinimumFreeGb
    {
        get => _minimumFreeGb;
        set
        {
            if (SetField(ref _minimumFreeGb, value))
                OnPropertyChanged(nameof(MinimumFreeValue));
        }
    }

    public double MinimumFreeValue
    {
        get => MinimumFreeGb;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;
            MinimumFreeGb = (int)Math.Clamp(Math.Round(value), 1, 512);
        }
    }

    public int MemoryBufferMb
    {
        get => _memoryBufferMb;
        set
        {
            if (value < 64 || value > 8192) return;
            if (SetField(ref _memoryBufferMb, value))
                OnPropertyChanged(nameof(MemoryBufferValue));
        }
    }

    public double MemoryBufferValue
    {
        get => MemoryBufferMb;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;
            MemoryBufferMb = (int)Math.Clamp(Math.Round(value), 64, 8192);
        }
    }

    public bool AudioEnabled { get => _audioEnabled; set => SetField(ref _audioEnabled, value); }
    public bool MicrophoneEnabled { get => _microphoneEnabled; set => SetField(ref _microphoneEnabled, value); }
    public bool SeparateAudioTracks { get => _separateAudioTracks; set => SetField(ref _separateAudioTracks, value); }
    public bool EmbedTelemetryFooter { get => _embedTelemetryFooter; set => SetField(ref _embedTelemetryFooter, value); }

    public string SaveMessage
    {
        get => _saveMessage;
        private set
        {
            if (!SetField(ref _saveMessage, value))
                return;
            OnPropertyChanged(nameof(HasSaveMessage));
        }
    }

    public bool HasSaveMessage => !string.IsNullOrWhiteSpace(SaveMessage);
    public bool SaveFailed { get => _saveFailed; private set => SetField(ref _saveFailed, value); }
    public string MemoryBudgetWarning
    {
        get
        {
            var estimate = new AppConfig
            {
                MaxBitrate = MaxBitrate,
                PreRollSeconds = PreRoll,
                PostRollSeconds = PostRoll,
                SegmentGraceSeconds = SegmentGrace
            }.EstimatedBufferMb;
            return estimate > MemoryBufferMb
                ? $"按最大视频码率估算，完整缓存约需 {estimate:0} MB；当前上限为 {MemoryBufferMb} MB，部分回溯内容可能提前淘汰。"
                : "";
        }
    }
    public bool HasMemoryBudgetWarning => IsHighlightMode && MemoryBudgetWarning.Length > 0;

    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand SelectOutputFolderCommand { get; }
    public ICommand SelectFfmpegCommand { get; }

    private void Save()
    {
        try
        {
            var candidate = _config.Clone();
            candidate.FfmpegPath = FfmpegPath.Trim();
            candidate.OutputDir = OutputDir.Trim();
            candidate.CaptureMode = CaptureMode;
            candidate.RecordingMode = RecordingMode;
            candidate.ReviewAfterMatch = ReviewAfterMatch;
            candidate.DeleteToRecycleBin = DeleteToRecycleBin;
            candidate.RecordingEvents = RecordingEvents.Where(choice => choice.IsSelected).Select(choice => choice.Id).ToArray();
            candidate.ShowEventNotifications = ShowEventNotifications;
            candidate.EventNotificationStyle = EventNotificationStyle;
            candidate.PollIntervalSeconds = _pollInterval;
            candidate.TelemetryIntervalSeconds = TelemetryInterval;
            candidate.AudioCaptureMode = "process_loopback";
            candidate.Encoder = NormalizeEncoder(Encoder);
            candidate.Fps = Fps;
            candidate.Bitrate = Bitrate.Trim();
            candidate.MaxBitrate = MaxBitrate.Trim();
            candidate.BufferSize = BufferSize.Trim();
            candidate.PreRollSeconds = PreRoll;
            candidate.PostRollSeconds = PostRoll;
            candidate.SegmentGraceSeconds = SegmentGrace;
            candidate.DiskQuotaGb = DiskQuotaGb;
            candidate.MinimumFreeGb = MinimumFreeGb;
            candidate.MemoryBufferMb = MemoryBufferMb;
            candidate.AudioEnabled = AudioEnabled;
            candidate.MicrophoneEnabled = MicrophoneEnabled;
            candidate.SeparateAudioTracks = SeparateAudioTracks;
            candidate.EmbedTelemetryFooter = EmbedTelemetryFooter;

            _settingsService.Save(candidate);
            _savedDraft = DraftFingerprint();
            NotifySaveState();
            SaveFailed = false;
            SaveMessage = "设置已保存";
            OnPropertyChanged(nameof(OutputPathPreview));
        }
        catch (Exception exception)
        {
            SaveFailed = true;
            SaveMessage = $"保存失败：{exception.Message}";
        }
    }

    private void ResetToDefaults()
    {
        var defaults = new AppConfig();
        FfmpegPath = defaults.FfmpegPath;
        OutputDir = defaults.OutputDir;
        CaptureMode = defaults.CaptureMode;
        RecordingMode = defaults.RecordingMode;
        ReviewAfterMatch = defaults.ReviewAfterMatch;
        DeleteToRecycleBin = defaults.DeleteToRecycleBin;
        ShowEventNotifications = defaults.ShowEventNotifications;
        EventNotificationStyle = defaults.EventNotificationStyle;
        TelemetryInterval = defaults.TelemetryIntervalSeconds;
        foreach (var choice in RecordingEvents) choice.IsSelected = defaults.RecordingEvents.Contains(choice.Id);
        Encoder = defaults.Encoder;
        Fps = defaults.Fps;
        Bitrate = defaults.Bitrate;
        MaxBitrate = defaults.MaxBitrate;
        BufferSize = defaults.BufferSize;
        PreRoll = defaults.PreRollSeconds;
        PostRoll = defaults.PostRollSeconds;
        SegmentGrace = defaults.SegmentGraceSeconds;
        DiskQuotaGb = defaults.DiskQuotaGb;
        MinimumFreeGb = defaults.MinimumFreeGb;
        MemoryBufferMb = defaults.MemoryBufferMb;
        AudioEnabled = defaults.AudioEnabled;
        MicrophoneEnabled = defaults.MicrophoneEnabled;
        SeparateAudioTracks = defaults.SeparateAudioTracks;
        EmbedTelemetryFooter = defaults.EmbedTelemetryFooter;
        SaveFailed = false;
        SaveMessage = HasUnsavedChanges ? "已恢复默认值，点击保存生效" : "当前已是默认设置";
    }

    private async Task PickPathAsync(bool executable)
    {
        if (IsPickingPath || _disposed) return;
        IsPickingPath = true;
        try
        {
            if (_pickers == null) throw new InvalidOperationException("文件选择器尚未就绪");
            var selected = executable ? await _pickers.PickFfmpegExecutableAsync() : await _pickers.PickOutputFolderAsync();
            if (_disposed || string.IsNullOrWhiteSpace(selected)) return;
            if (executable)
            {
                if (!Path.GetExtension(selected).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("请选择 FFmpeg 的 .exe 可执行文件");
                FfmpegPath = selected;
            }
            else OutputDir = selected;
            SaveFailed = false;
            SaveMessage = executable ? "已选择 FFmpeg，保存设置后生效" : "已选择录像目录，保存设置后生效";
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                SaveFailed = true;
                SaveMessage = $"选择失败：{error.Message}";
            }
        }
        finally { IsPickingPath = false; }
    }

    private void SessionStatusChanged(object? sender, RecordingStatusChangedEventArgs args)
    {
        void Apply()
        {
            if (_disposed || _wasListening == IsListening) return;
            _wasListening = IsListening;
            OnPropertyChanged(nameof(IsListening));
            NotifySaveState();
        }
        if (_dispatcher is { HasThreadAccess: false }) _dispatcher.TryEnqueue(Apply);
        else Apply();
    }

    private void NotifySaveState()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SettingsStateText));
    }

    private string DraftFingerprint() => JsonSerializer.Serialize(new
    {
        FfmpegPath,
        OutputDir,
        Encoder,
        Fps,
        Bitrate,
        MaxBitrate,
        BufferSize,
        PreRoll,
        PostRoll,
        SegmentGrace,
        DiskQuotaGb,
        MinimumFreeGb,
        MemoryBufferMb,
        AudioEnabled,
        MicrophoneEnabled,
        SeparateAudioTracks,
        EmbedTelemetryFooter,
        CaptureMode,
        RecordingMode,
        ReviewAfterMatch,
        DeleteToRecycleBin,
        ShowEventNotifications,
        EventNotificationStyle,
        TelemetryInterval,
        PollInterval = _pollInterval,
        RecordingEvents = RecordingEvents.Where(choice => choice.IsSelected).Select(choice => choice.Id).ToArray()
    });

    private static double RateMbps(string rate)
    {
        rate = rate.Trim();
        if (rate.Length == 0 || !double.TryParse(rate.TrimEnd('k', 'K', 'm', 'M'),
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return 0;
        return char.ToUpperInvariant(rate[^1]) switch { 'M' => value, 'K' => value / 1000, _ => value / 1_000_000 };
    }
    private static string FormatMbps(double value) => value.ToString("0.######", CultureInfo.InvariantCulture) + "M";

    public void Dispose()
    {
        _disposed = true;
        if (_session != null) _session.StatusChanged -= SessionStatusChanged;
        foreach (var choice in RecordingEvents) choice.PropertyChanged -= EventSelectionChanged;
    }

    private void OpenOutputFolder()
    {
        try
        {
            var directory = _paths?.ResolveOutputDirectory(OutputDir) ?? OutputDir;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SaveFailed = true;
            SaveMessage = $"无法打开输出目录：{exception.Message}";
        }
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Load();
        FfmpegPath = settings.FfmpegPath;
        OutputDir = settings.OutputDir;
        CaptureMode = settings.CaptureMode;
        RecordingMode = settings.RecordingMode;
        ReviewAfterMatch = settings.ReviewAfterMatch;
        DeleteToRecycleBin = settings.DeleteToRecycleBin;
        ShowEventNotifications = settings.ShowEventNotifications;
        EventNotificationStyle = settings.EventNotificationStyle;
        TelemetryInterval = settings.TelemetryIntervalSeconds;
        _pollInterval = settings.PollIntervalSeconds;
        foreach (var choice in RecordingEvents) choice.IsSelected = settings.RecordingEvents.Contains(choice.Id);
        Encoder = NormalizeEncoder(settings.Encoder);
        Fps = settings.Fps;
        Bitrate = settings.Bitrate;
        MaxBitrate = settings.MaxBitrate;
        BufferSize = settings.BufferSize;
        PreRoll = settings.PreRollSeconds;
        PostRoll = settings.PostRollSeconds;
        SegmentGrace = settings.SegmentGraceSeconds;
        DiskQuotaGb = settings.DiskQuotaGb;
        MinimumFreeGb = settings.MinimumFreeGb;
        MemoryBufferMb = settings.MemoryBufferMb;
        AudioEnabled = settings.AudioEnabled;
        MicrophoneEnabled = settings.MicrophoneEnabled;
        SeparateAudioTracks = settings.SeparateAudioTracks;
        EmbedTelemetryFooter = settings.EmbedTelemetryFooter;
    }

    private static string NormalizeEncoder(string? encoder) => encoder?.Trim().ToLowerInvariant() switch
    {
        "amd" or "h264_amf" => "amf",
        "nvidia" or "h264_nvenc" => "nvenc",
        "intel" or "h264_qsv" => "qsv",
        "software" or "libx264" => "x264",
        var value when !string.IsNullOrEmpty(value) => value,
        _ => "auto"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void EventSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        SaveMessage = "";
        SaveFailed = false;
        NotifySaveState();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        if (name == nameof(RecordingMode))
        {
            OnPropertyChanged(nameof(IsHighlightMode));
            OnPropertyChanged(nameof(HasMemoryBudgetWarning));
        }
        if (name == nameof(TelemetryInterval)) OnPropertyChanged(nameof(TelemetryIntervalMilliseconds));
        if (name == nameof(CaptureMode)) OnPropertyChanged(nameof(CaptureModeHint));
        if (name is nameof(MaxBitrate) or nameof(PreRoll) or nameof(PostRoll) or nameof(SegmentGrace) or nameof(MemoryBufferMb))
        {
            OnPropertyChanged(nameof(MemoryBudgetWarning));
            OnPropertyChanged(nameof(HasMemoryBudgetWarning));
        }
        if (name is nameof(Bitrate) or nameof(MaxBitrate) or nameof(BufferSize))
        {
            OnPropertyChanged(nameof(VideoBitrateMbps));
            OnPropertyChanged(nameof(MaxBitrateMbps));
            OnPropertyChanged(nameof(RateBufferMegabits));
            OnPropertyChanged(nameof(SizeEstimate));
        }
        if (name is nameof(AudioEnabled) or nameof(MicrophoneEnabled)) OnPropertyChanged(nameof(CanSeparateAudioTracks));
        if (name == nameof(IsPickingPath)) OnPropertyChanged(nameof(CanEditPaths));
        if (_savedDraft != null && HasUnsavedChanges && name is not (nameof(SaveMessage) or nameof(SaveFailed) or nameof(IsPickingPath)))
        {
            _saveMessage = "";
            _saveFailed = false;
            OnPropertyChanged(nameof(SaveMessage));
            OnPropertyChanged(nameof(HasSaveMessage));
            OnPropertyChanged(nameof(SaveFailed));
        }
        NotifySaveState();
        return true;
    }
}
