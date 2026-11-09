using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using LolHighlight.Desktop.Services;
using LolHighlight.Desktop.ViewModels;

namespace LolHighlight.Desktop.Views;

public sealed partial class HomePage : UserControl
{
    private readonly HomeViewModel _vm;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _previewTimer;
    private LiveRecordingPreview? _preview;
    private byte[]? _latestFrame;
    private bool _active, _displaying;
    private int _previewVersion;
    private Task _stopping = Task.CompletedTask;

    public HomePage()
    {
        InitializeComponent();
        _vm = App.GetService<HomeViewModel>(); DataContext = _vm;
        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromSeconds(1d / LiveRecordingPreview.FramesPerSecond);
        _previewTimer.Tick += async (_, _) =>
        {
            _vm.RefreshAudioLevels();
            await ShowLatestFrameAsync();
        };
        _vm.PropertyChanged += Changed;
        Loaded += (_, _) => SetActive(Visibility == Visibility.Visible);
        Unloaded += (_, _) => SetActive(false);
    }

    public void SetActive(bool active) { _active = active; UpdatePreview(); }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(HomeViewModel.Status)) UpdatePreview(); }

    private void UpdatePreview()
    {
        var wanted = _active && _vm.Status is RecordingStatus.Starting or RecordingStatus.Recording;
        if (!wanted)
        {
            StopPreview();
            PreviewHint.Text = _vm.Status == RecordingStatus.WaitingForGame ? "入局后显示采集画面" : "录制画面将在这里显示";
            return;
        }
        if (_preview != null) return;
        var version = ++_previewVersion;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewHint.Text = "正在连接录制画面";
        _preview = new LiveRecordingPreview(App.GetService<RecordingPreviewTap>(), App.GetService<AppConfig>(),
            frame => { if (Volatile.Read(ref _previewVersion) == version) Interlocked.Exchange(ref _latestFrame, frame); },
            message => DispatcherQueue.TryEnqueue(() =>
            {
                if (_previewVersion != version) return;
                RecordingImage.Source = null;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                PreviewHint.Text = message;
            }));
        _previewTimer.Start();
    }

    private async Task ShowLatestFrameAsync()
    {
        if (_displaying || !_active || _preview == null) return;
        var frame = Interlocked.Exchange(ref _latestFrame, null);
        if (frame == null) return;
        _displaying = true;
        var version = _previewVersion;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            { writer.WriteBytes(frame); await writer.StoreAsync(); }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (_previewVersion != version || !_active) return;
            RecordingImage.Source = bitmap;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) when (e is IOException or System.Runtime.InteropServices.COMException or ArgumentException) { }
        finally { _displaying = false; }
    }

    private void StopPreview()
    {
        _previewTimer.Stop();
        Interlocked.Increment(ref _previewVersion);
        Interlocked.Exchange(ref _latestFrame, null);
        RecordingImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        var previous = _preview;
        _preview = null;
        if (previous != null) _stopping = Task.WhenAll(_stopping, previous.StopAsync());
    }
    public async Task StopAsync() { _active = false; StopPreview(); await _stopping; }

    private void Start_Click(object sender, RoutedEventArgs e)
        => ((Application.Current as App)?.MainWindow as MainWindow)?.StartRecording();

    private void StartWeGame_Click(object sender, RoutedEventArgs e)
    {
        GameLaunchFeedback.IsOpen = false;
        try { LeagueGameLauncher.StartWithWeGame(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowGameLaunchFeedback("WeGame 启动失败", error.Message);
        }
    }

    private async void StartLeagueClient_Click(object sender, RoutedEventArgs e)
        => await StartLeagueClientAsync(chooseClient: false);

    private async void ChooseLeagueClient_Click(object sender, RoutedEventArgs e)
        => await StartLeagueClientAsync(chooseClient: true);

    private async Task StartLeagueClientAsync(bool chooseClient)
    {
        GameLaunchFeedback.IsOpen = false;
        GameLaunchButton.IsEnabled = false;
        try
        {
            var launcher = App.GetService<LeagueGameLauncher>();
            var path = chooseClient ? null : await Task.Run(launcher.FindClient);
            var picked = path == null;
            path ??= await App.GetService<ISettingsPickerService>().PickLeagueClientAsync();
            if (path == null) return;

            LeagueGameLauncher.StartClient(path);
            if (picked && !launcher.RememberClient(path))
                ShowGameLaunchFeedback("无法记住客户端路径", "已发送启动请求，下次启动时可能需要重新选择客户端。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            ShowGameLaunchFeedback("普通启动失败", error.Message);
        }
        finally { GameLaunchButton.IsEnabled = true; }
    }

    private void ShowGameLaunchFeedback(string title, string message)
    {
        GameLaunchFeedback.Title = title;
        GameLaunchFeedback.Message = message;
        GameLaunchFeedback.IsOpen = true;
    }

    internal void SetPendingReviewCount(int count)
    {
        PendingReviewNotice.Message = count > 0 ? $"有 {count} 场对局等待确认" : "";
        PendingReviewNotice.IsOpen = count > 0;
    }

    private void ReviewPending_Click(object sender, RoutedEventArgs e)
        => ((Application.Current as App)?.MainWindow as MainWindow)?.ReviewPending();
}

