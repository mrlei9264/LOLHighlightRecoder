using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IRecordingSessionService _session;
    private readonly DispatcherQueue? _dispatcherQueue;
    private RecordingSnapshot _snapshot;
    private string _currentPage = "home";
    private bool _disposed;
    private bool _libraryPlaying, _gradingPlaying, _generating;
    private double _gradeProgress;

    public MainViewModel(IRecordingSessionService session)
    {
        _session = session;
        _snapshot = session.Snapshot;
        _dispatcherQueue = SafeGetDispatcherQueue();
        _session.StatusChanged += SessionOnStatusChanged;
        StartCommand = new AsyncRelayCommand(StartAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
    }

    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            if (_currentPage == value)
                return;
            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageHint));
        }
    }

    public string PageTitle => CurrentPage switch { "grading" => "视频调色", "library" => "视频库", "settings" => "录制设置", _ => "录制控制" };
    public string PageHint => CurrentPage switch { "grading" => "选择色调 · 一键生成", "library" => "浏览、预览与管理原片", "settings" => "采集、事件与存储", _ => "捕捉每一次精彩操作" };
    public RecordingStatus Status => _snapshot.Status;
    public string StatusLabel => _snapshot.Status.ToLabel();
    public string StatusTitle => _snapshot.Title;
    public string StatusDetail => _snapshot.Detail;
    public string BufferText => $"{_snapshot.BufferedSeconds:0.0} 秒";
    public string BufferLine => $"本地缓存 {BufferText}";
    public bool IsRunning => _snapshot.Status.IsActive();
    public bool IsBusy => _snapshot.Status is RecordingStatus.Starting or RecordingStatus.Stopping or RecordingStatus.Exporting;
    public bool CanStart => !IsRunning && !IsBusy;
    public bool CanStop => IsRunning && !IsBusy;
    public bool CanToggleRecording => CanStart || CanStop;
    public string RecordingActionLabel => IsRunning ? "停止录制" : "开始录制";
    public string WorkStatus => _generating ? $"生成中 · {_gradeProgress:0}%" :
        IsRunning || Status == RecordingStatus.Error ? StatusLabel : _libraryPlaying || _gradingPlaying ? "播放中" : "空闲";
    public string WorkStatusColor => Status == RecordingStatus.Error ? "#F08080" :
        _generating || _libraryPlaying || _gradingPlaying || Status == RecordingStatus.Recording ? "#76D5CA" : IsRunning ? "#E6C868" : "#858B91";
    public void SetPlaybackActivity(bool library, bool playing)
    { if (library) _libraryPlaying = playing; else _gradingPlaying = playing; OnPropertyChanged(nameof(WorkStatus)); OnPropertyChanged(nameof(WorkStatusColor)); }
    public void SetGradingActivity(bool generating, double progress)
    { _generating = generating; _gradeProgress = progress; OnPropertyChanged(nameof(WorkStatus)); OnPropertyChanged(nameof(WorkStatusColor)); }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    private async Task StartAsync()
    {
        try { await _session.StartAsync(); }
        catch { }
    }

    private async Task StopAsync()
    {
        try { await _session.StopAsync(); }
        catch { }
    }

    private void SessionOnStatusChanged(object? sender, RecordingStatusChangedEventArgs e)
    {
        void Apply()
        {
            if (_disposed) return;
            _snapshot = e.Snapshot;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusTitle));
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(BufferText));
            OnPropertyChanged(nameof(BufferLine));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(CanToggleRecording));
            OnPropertyChanged(nameof(RecordingActionLabel));
            OnPropertyChanged(nameof(WorkStatus));
            OnPropertyChanged(nameof(WorkStatusColor));
        }

        var dispatcher = _dispatcherQueue;
        if (dispatcher != null && !dispatcher.HasThreadAccess)
            dispatcher.TryEnqueue(Apply);
        else
            Apply();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.StatusChanged -= SessionOnStatusChanged;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static DispatcherQueue? SafeGetDispatcherQueue()
    {
        try { return DispatcherQueue.GetForCurrentThread(); }
        catch { return null; }
    }
}
