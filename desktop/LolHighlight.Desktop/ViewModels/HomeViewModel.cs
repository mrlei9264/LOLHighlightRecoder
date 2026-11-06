using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.ViewModels;

public class HomeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IRecordingSessionService _session;
    private readonly IVideoScanner _videoScanner;
    private readonly AppPaths _paths;
    private readonly AppConfig _config;
    private readonly DispatcherQueue? _dispatcherQueue;
    private DispatcherQueueTimer? _refreshTimer;
    private bool _disposed;
    private bool _refreshing;
    private TimeSpan _lastCpu;
    private DateTime _resourceSample = DateTime.UtcNow;
    private string _cpu = "—", _memory = "—", _disk = "—", _uptime = "—";
    private double _cpuPercent;
    private string _lastState = "";
    public string CpuUsage { get => _cpu; private set => SetField(ref _cpu, value); }
    public double CpuPercent { get => _cpuPercent; private set => SetField(ref _cpuPercent, value); }
    public string MemoryUsage { get => _memory; private set => SetField(ref _memory, value); }
    public string DiskFree { get => _disk; private set => SetField(ref _disk, value); }
    public string Uptime { get => _uptime; private set => SetField(ref _uptime, value); }
    public string FrameRate => $"{_config.Fps} FPS";
    public string RecordingMode => _config.RecordingMode == "full_game" ? "整局" : "高光";
    public string AudioState => _config.AudioEnabled ? "已开启" : "关闭";
    public string MicrophoneState => _config.MicrophoneEnabled ? "已开启" : "关闭";
    public string TriggerRules => string.Join(" · ", RecordingEventCatalog.All.Where(e => _config.RecordingEvents.Contains(e.Id)).Select(e => e.Label));
    public string MemoryBudget => $"{_config.MemoryBufferMb} MB";
    public ObservableCollection<DashboardState> StateChanges { get; } = new();

    private RecordingSnapshot _snapshot;
    private string _outputDirectory = "";
    private string _captureSummary = "";
    private string _encoderSummary = "";
    private string _clipWindowSummary = "";
    private string _audioSummary = "";
    private bool _hasRecentVideos;

    public HomeViewModel(
        IRecordingSessionService session,
        IVideoScanner videoScanner,
        AppPaths paths,
        AppConfig config)
    {
        _session = session;
        _videoScanner = videoScanner;
        _paths = paths;
        _config = config;
        _dispatcherQueue = SafeGetDispatcherQueue();
        _snapshot = session.Snapshot;

        StartCommand = new AsyncRelayCommand(StartAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);

        RefreshEnvironment();
        ApplySnapshot(_session.Snapshot);
        if (_dispatcherQueue == null) LoadRecentVideos();
        else _ = RefreshRecentVideosAsync();

        _session.StatusChanged += SessionOnStatusChanged;
        StartRefreshTimer();
        RefreshResources();
    }

    public RecordingStatus Status => _snapshot.Status;
    public string StatusLabel => _snapshot.Status.ToLabel();
    public string StatusGlyph => _snapshot.Status.ToGlyph();
    public string StatusTitle => _snapshot.Title;
    public string StatusDetail => _snapshot.Status == RecordingStatus.Recording
        ? $"{_snapshot.Detail} · 已缓存 {_snapshot.BufferedSeconds:0.0} 秒"
        : _snapshot.Detail;
    public bool IsStartVisible => _snapshot.Status is RecordingStatus.Idle or RecordingStatus.Error;
    public bool IsStopVisible => _snapshot.Status.IsActive();
    public bool IsBusy => _snapshot.Status is RecordingStatus.Starting or RecordingStatus.Stopping or RecordingStatus.Exporting;
    public bool CanToggleRecording => !IsBusy;
    public bool IsRecording => _snapshot.Status == RecordingStatus.Recording;
    public bool IsWaiting => _snapshot.Status is RecordingStatus.WaitingForGame or RecordingStatus.Paused or RecordingStatus.Retrying;
    public bool IsError => _snapshot.Status == RecordingStatus.Error;
    public bool HasError => IsError && !string.IsNullOrWhiteSpace(_snapshot.Detail);
    public bool HasWarning => !IsError && !string.IsNullOrWhiteSpace(_snapshot.LastError);
    public string WarningMessage => _snapshot.LastError ?? "";
    public double BufferedSeconds => _snapshot.BufferedSeconds;
    public string BufferText => $"{_snapshot.BufferedSeconds:0.0} 秒";
    public string BufferCapacityText => $"保留最近 {BufferCapacitySeconds:0} 秒";
    public double BufferCapacitySeconds => _config.RecordingMode == "full_game" ? Math.Ceiling(2 * _config.SegmentSeconds + 4) : _config.RetentionSeconds;
    public double BufferPercent => Math.Clamp(_snapshot.BufferedSeconds / BufferCapacitySeconds * 100, 0, 100);
    public string PlayerName => _snapshot.Player?.GameName ?? "等待对局";
    public string PlayerHint => _snapshot.Player?.RiotId ?? "进入对局后自动识别";

    public string CaptureSummary { get => _captureSummary; private set => SetField(ref _captureSummary, value); }
    public string EncoderSummary { get => _encoderSummary; private set => SetField(ref _encoderSummary, value); }
    public string ClipWindowSummary { get => _clipWindowSummary; private set => SetField(ref _clipWindowSummary, value); }
    public string AudioSummary { get => _audioSummary; private set => SetField(ref _audioSummary, value); }
    public string OutputDirectory { get => _outputDirectory; private set => SetField(ref _outputDirectory, value); }
    public bool HasRecentVideos { get => _hasRecentVideos; private set => SetField(ref _hasRecentVideos, value); }
    public string RecentCountText => RecentVideos.Count == 0 ? "暂无高光" : $"{RecentVideos.Count} 个片段";

    public ObservableCollection<VideoItem> RecentVideos { get; } = new();

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }

    private async Task StartAsync()
    {
        try
        {
            await _session.StartAsync();
        }
        catch
        {
            ApplySnapshot(_session.Snapshot);
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await _session.StopAsync();
        }
        catch
        {
            ApplySnapshot(_session.Snapshot);
        }
        finally
        {
            await RefreshRecentVideosAsync();
        }
    }

    private void SessionOnStatusChanged(object? sender, RecordingStatusChangedEventArgs e)
    {
        if (_disposed) return;
        if (_dispatcherQueue is { } dispatcher && !dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(() => ApplySnapshot(e.Snapshot));
            return;
        }

        ApplySnapshot(e.Snapshot);
    }

    private void ApplySnapshot(RecordingSnapshot snapshot)
    {
        if (_disposed) return;
        var previousStatus = _snapshot.Status;
        _snapshot = snapshot;
        if (_lastState != snapshot.Status + snapshot.Title)
        {
            _lastState = snapshot.Status + snapshot.Title;
            StateChanges.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), snapshot.Status.ToLabel(), snapshot.Title));
            while (StateChanges.Count > 20) StateChanges.RemoveAt(StateChanges.Count - 1);
        }

        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(IsStartVisible));
        OnPropertyChanged(nameof(IsStopVisible));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanToggleRecording));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(WarningMessage));
        OnPropertyChanged(nameof(BufferedSeconds));
        OnPropertyChanged(nameof(BufferText));
        OnPropertyChanged(nameof(BufferCapacityText));
        OnPropertyChanged(nameof(BufferCapacitySeconds));
        OnPropertyChanged(nameof(BufferPercent));
        OnPropertyChanged(nameof(PlayerName));
        OnPropertyChanged(nameof(PlayerHint));

        // 状态每 200ms 更新一次缓冲秒数；只在关键状态切换时扫描磁盘，
        // 避免录制期间每秒多次读取录像目录。
        if (previousStatus != snapshot.Status &&
            snapshot.Status is RecordingStatus.Recording or RecordingStatus.Idle or RecordingStatus.Error)
        {
            _ = RefreshRecentVideosAsync();
        }
    }

    private void RefreshEnvironment()
    {
        OutputDirectory = _paths.ResolveOutputDirectory(_config.OutputDir);
        CaptureSummary = $"LOL 游戏窗口 · {_config.Fps} FPS";
        EncoderSummary = $"{EncoderName(_config.Encoder)} · {_config.Bitrate}";
        ClipWindowSummary = _config.RecordingMode == "full_game" ? "整局连续录制" : $"{_config.PreRollSeconds:0} 秒前 · {_config.PostRollSeconds:0} 秒后";
        AudioSummary = BuildAudioSummary();
        foreach (var property in new[] { nameof(FrameRate), nameof(RecordingMode), nameof(AudioState), nameof(MicrophoneState), nameof(TriggerRules), nameof(MemoryBudget) }) OnPropertyChanged(property);
    }

    private void RefreshResources()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var now = DateTime.UtcNow; var elapsed = (now - _resourceSample).TotalSeconds;
            var totalCpu = process.TotalProcessorTime;
            if (_lastCpu != TimeSpan.Zero && elapsed > 0)
            { CpuPercent = Math.Clamp((totalCpu - _lastCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100, 0, 100); CpuUsage = $"{CpuPercent:0.0}%"; }
            else CpuUsage = "0.0%";
            _lastCpu = totalCpu; _resourceSample = now;
            MemoryUsage = $"{process.WorkingSet64 / 1048576d:0} MB";
            Uptime = (DateTime.Now - process.StartTime).ToString(@"hh\:mm\:ss");
            var drive = new DriveInfo(Path.GetPathRoot(OutputDirectory)!);
            DiskFree = $"{drive.AvailableFreeSpace / 1073741824d:0.0} GB";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException) { DiskFree = "不可用"; }
    }

    private void LoadRecentVideos()
    {
        var latest = _videoScanner.ScanRecentVideos(OutputDirectory, 5);
        ApplyRecentVideos(latest);
    }

    private async Task RefreshRecentVideosAsync()
    {
        if (_disposed || _refreshing) return;
        _refreshing = true;
        var directory = OutputDirectory;
        try
        {
            var latest = await Task.Run(() => _videoScanner.ScanRecentVideos(directory, 5));
            if (!_disposed && directory == OutputDirectory) ApplyRecentVideos(latest);
        }
        catch (Exception) { /* A transient directory error must not stop status updates. */ }
        finally { _refreshing = false; }
    }

    private void ApplyRecentVideos(List<VideoItem> latest)
    {
        if (latest.SequenceEqual(RecentVideos))
            return;

        RecentVideos.Clear();
        foreach (var video in latest)
            RecentVideos.Add(video);

        HasRecentVideos = RecentVideos.Count > 0;
        OnPropertyChanged(nameof(RecentCountText));
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            Process.Start(new ProcessStartInfo { FileName = OutputDirectory, UseShellExecute = true });
        }
        catch
        {
            // 输出目录暂时不可访问时保持界面稳定，用户仍可在设置页查看路径。
        }
    }

    private string BuildAudioSummary()
    {
        if (!_config.AudioEnabled)
            return _config.MicrophoneEnabled ? "麦克风" : "仅视频";

        var summary = _config.MicrophoneEnabled ? "游戏音频 + 麦克风" : "游戏音频";
        if (_config.MicrophoneEnabled && _config.SeparateAudioTracks)
            summary += " · 分轨";
        return summary;
    }

    private static string EncoderName(string encoder) => encoder.ToLowerInvariant() switch
    {
        "nvenc" or "h264_nvenc" => "NVIDIA NVENC",
        "amf" or "h264_amf" => "AMD AMF",
        "qsv" or "h264_qsv" => "Intel QSV",
        "x264" or "libx264" => "软件 x264",
        _ => "自动编码"
    };

    private void StartRefreshTimer()
    {
        if (_dispatcherQueue == null)
            return;

        _refreshTimer = _dispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(4);
        _refreshTimer.Tick += RefreshTimerOnTick;
        _refreshTimer.Start();
    }

    private async void RefreshTimerOnTick(DispatcherQueueTimer sender, object args)
    {
        RefreshEnvironment();
        RefreshResources();
        await RefreshRecentVideosAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.StatusChanged -= SessionOnStatusChanged;
        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimerOnTick;
            _refreshTimer = null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }

    private static DispatcherQueue? SafeGetDispatcherQueue()
    {
        try { return DispatcherQueue.GetForCurrentThread(); }
        catch { return null; }
    }
}

public sealed record DashboardState(string Time, string Status, string Title);
