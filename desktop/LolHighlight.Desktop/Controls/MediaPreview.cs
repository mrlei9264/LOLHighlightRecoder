using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace LolHighlight.Desktop.Controls;

/// <summary>Shared player with event chapters and an optional GPU color lookup table.</summary>
public sealed class MediaPreview : Grid
{
    private readonly MediaPlayerElement _video = new() { AreTransportControlsEnabled = false, Stretch = Stretch.Uniform };
    private readonly Grid _bar = new()
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(245, 34, 34, 34)),
        CornerRadius = new CornerRadius(0, 0, 7, 7),
        Padding = new Thickness(8, 0, 8, 4),
        Opacity = 0,
        VerticalAlignment = VerticalAlignment.Bottom
    };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, StepFrequency = .1, MinHeight = 28, IsThumbToolTipEnabled = false };
    private const double ChapterTargetSize = 24;
    private const double ChapterSeekTolerance = 0.002;
    private readonly Canvas _chapterMarkers = new() { Height = ChapterTargetSize, Visibility = Visibility.Collapsed };
    private readonly TextBlock _time = new() { Text = "00:00 / 00:00", FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _volumeSlider;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _positionTimer;
    private bool _updatingSeek, _controlsBelow;
    private readonly Button _play;
    private readonly Button _tracks;
    private readonly Button _full;
    private readonly Button _previousChapter;
    private readonly Button _nextChapter;
    private IReadOnlyList<PlaybackChapter> _chapters = [];
    private CancellationTokenSource? _chapterLoad;
    private double _chapterDuration;
    private MediaPlayer? _player;
    private MediaSource? _source;
    private MediaPlaybackItem? _item;
    private CanvasControl? _canvas;
    private CanvasRenderTarget? _frame;
    private EffectTransferTable3D? _table;
    private TableTransfer3DEffect? _effect;
    private ColorTone? _tone;
    private double _strength;
    private int _queuedFrame;
    private bool _hasFrame, _grading;
    private bool _pointerOverBar;
    private bool _primeFrame;
    private TimeSpan? _startPosition;
    public event Action<string>? PlaybackFailed;
    public event Action? ExitRequested;
    public event Action<bool>? PlaybackChanged;
    public bool IsFullScreen { get; internal set; }
    public bool AllowFullScreen { get => _full.IsEnabled; set => _full.IsEnabled = value; }
    public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing && !_primeFrame;
    public TimeSpan Position => _player?.PlaybackSession.Position ?? TimeSpan.Zero;
    public bool ControlsBelowVideo
    {
        get => _controlsBelow;
        set
        {
            _controlsBelow = value;
            RowDefinitions.Clear();
            if (value) { RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto }); }
            Grid.SetRow(_bar, value ? 1 : 0); _bar.Opacity = value ? 1 : 0;
        }
    }

    public MediaPreview()
    {
        Background = new SolidColorBrush(Colors.Black);
        Children.Add(_video);
        _play = IconButton("\uE768", "播放 / 暂停");
        _play.Click += (_, _) => TogglePlay();
        var volume = IconButton("\uE767", "音量");
        var slider = _volumeSlider = new Slider { Minimum = 0, Maximum = 100, Value = 70, Width = 150, Header = "音量" };
        slider.ValueChanged += (_, e) => { if (_player != null) _player.Volume = e.NewValue / 100; };
        volume.Flyout = new Flyout { Content = slider };
        _tracks = IconButton("\uE8D6", "选择音轨");
        _tracks.Click += (_, _) => ShowAudioTracks();
        var full = _full = IconButton("\uE740", "全屏 / 退出全屏");
        full.Click += (_, _) => ToggleFullScreen();
        _bar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _bar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) _bar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _bar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _bar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _previousChapter = IconButton("\uE892", "上一事件");
        _nextChapter = IconButton("\uE893", "下一事件");
        _previousChapter.Visibility = _nextChapter.Visibility = Visibility.Collapsed;
        _previousChapter.Click += (_, _) => SeekChapter(forward: false);
        _nextChapter.Click += (_, _) => SeekChapter(forward: true);
        var timeline = new Grid();
        timeline.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        timeline.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        timeline.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var track = new Grid();
        track.RowDefinitions.Add(new() { Height = GridLength.Auto });
        track.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(_chapterMarkers, 1);
        track.Children.Add(_seek);
        track.Children.Add(_chapterMarkers);
        Grid.SetColumn(track, 1);
        Grid.SetColumn(_nextChapter, 2);
        timeline.Children.Add(_previousChapter);
        timeline.Children.Add(track);
        timeline.Children.Add(_nextChapter);
        Grid.SetColumnSpan(timeline, 5);
        _bar.Children.Add(timeline);
        _chapterMarkers.SizeChanged += (_, _) => PositionChapterMarkers();
        Grid.SetColumn(_time, 1);
        Grid.SetRow(_time, 1);
        _bar.Children.Add(_time);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_seek, "播放进度");
        _seek.ValueChanged += (_, e) => { if (!_updatingSeek) SeekTo(e.NewValue); };
        Grid.SetRow(_play, 1);
        _bar.Children.Add(_play);
        var buttons = new[] { volume, _tracks, full };
        for (var i = 0; i < buttons.Length; i++)
        {
            Grid.SetColumn(buttons[i], i + 2);
            Grid.SetRow(buttons[i], 1);
            _bar.Children.Add(buttons[i]);
        }
        // Keep the hit area present while transparent. Hovering the image never reveals it.
        _bar.PointerEntered += (_, _) => { _pointerOverBar = true; _bar.Opacity = 1; };
        _bar.PointerExited += (_, _) => { _pointerOverBar = false; if (!_controlsBelow) _bar.Opacity = 0; };
        _bar.GotFocus += (_, _) =>
        {
            if (_controlsBelow || _pointerOverBar || FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard }) _bar.Opacity = 1;
        };
        _bar.LostFocus += (_, _) => { if (!_pointerOverBar && !_controlsBelow) _bar.Opacity = 0; };
        Children.Add(_bar);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Space)
            {
                var focused = FocusManager.GetFocusedElement(XamlRoot);
                if (focused is Button button && (ReferenceEquals(button, _previousChapter) ||
                    ReferenceEquals(button, _nextChapter) || _chapterMarkers.Children.Contains(button))) return;
            }
            e.Handled = HandleKey(e.Key);
        };
        _positionTimer = DispatcherQueue.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(200);
        _positionTimer.Tick += (_, _) => UpdatePosition();
        _video.Tapped += (_, _) => { FocusPlayer(); TogglePlay(); };
        SizeChanged += (_, _) => _canvas?.Invalidate();
    }

    private static Button IconButton(string glyph, string name)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16 },
            Width = 34, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    public void SetGrade(ColorTone? tone, double strength)
    {
        _tone = tone; _strength = strength;
        if (!_grading)
        {
            _grading = true;
            _canvas = new CanvasControl { ClearColor = Colors.Black, IsHitTestVisible = false };
            _canvas.Draw += Draw;
            _canvas.CreateResources += (_, _) =>
            {
                ReleaseGpu();
                // Reparenting for fullscreen can recreate the canvas while playback is paused.
                if (_player != null && _player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused)
                { _primeFrame = true; _player.IsMuted = true; _player.Play(); }
                _canvas.Invalidate();
            };
            Children.Insert(1, _canvas);
            _video.Visibility = Visibility.Collapsed;
        }
        _table?.Dispose(); _table = null;
        _effect?.Dispose(); _effect = null;
        _canvas?.Invalidate();
    }

    public void Open(string path, bool autoPlay = false, TimeSpan? startPosition = null)
    {
        Release();
        if (!File.Exists(path)) return;
        try
        {
            _startPosition = startPosition;
            _primeFrame = _grading && !autoPlay;
            _player = new MediaPlayer { AutoPlay = autoPlay, Volume = _volumeSlider.Value / 100, IsMuted = _primeFrame, IsVideoFrameServerEnabled = _grading };
            _player.MediaFailed += Failed;
            _player.MediaOpened += Opened;
            _player.PlaybackSession.PlaybackStateChanged += StateChanged;
            if (_grading) _player.VideoFrameAvailable += FrameAvailable;
            else _video.SetMediaPlayer(_player);
            _source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path)));
            _item = new MediaPlaybackItem(_source);
            _player.Source = _item;
            _positionTimer.Start();
            _ = LoadChaptersAsync(path);
        }
        catch (Exception e) { Release(); PlaybackFailed?.Invoke(e.Message); }
    }

    private void Opened(MediaPlayer sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!ReferenceEquals(sender, _player)) return;
        if (_startPosition is { } position)
        {
            _startPosition = null;
            SeekTo(position.TotalSeconds);
        }
        _tracks.IsEnabled = _item?.AudioTracks.Count > 0;
        UpdatePosition();
        // Request one frame without audible playback, then retain it for paused grading.
        if (_grading && _primeFrame) sender.Play();
        _canvas?.Invalidate();
    });
    private void StateChanged(MediaPlaybackSession sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_player == null || !ReferenceEquals(sender, _player.PlaybackSession)) return;
        ((FontIcon)_play.Content).Glyph = sender.PlaybackState == MediaPlaybackState.Playing ? "\uE769" : "\uE768";
        PlaybackChanged?.Invoke(IsPlaying);
    });
    private void TogglePlay()
    {
        if (_player == null) return;
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
        else _player.Play();
    }
    public void FocusPlayer() => _play.Focus(FocusState.Programmatic);
    public bool HandleKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Escape:
                if (IsFullScreen) { ToggleFullScreen(); return true; }
                if (ExitRequested == null) return false;
                ExitRequested.Invoke();
                return true;
            case VirtualKey.Enter:
                if (!AllowFullScreen) return false;
                ToggleFullScreen();
                return true;
            case VirtualKey.Space: TogglePlay(); return true;
            case VirtualKey.Left: SeekTo((_player?.PlaybackSession.Position.TotalSeconds ?? 0) - 5); return true;
            case VirtualKey.Right: SeekTo((_player?.PlaybackSession.Position.TotalSeconds ?? 0) + 5); return true;
            case VirtualKey.Up: _volumeSlider.Value = Math.Min(100, _volumeSlider.Value + 5); return true;
            case VirtualKey.Down: _volumeSlider.Value = Math.Max(0, _volumeSlider.Value - 5); return true;
            default: return false;
        }
    }
    private void ToggleFullScreen()
    { if ((Application.Current as App)?.MainWindow is MainWindow window) window.TogglePlayerFullScreen(this); }
    private void SeekTo(double seconds)
    {
        if (_player == null || !_player.PlaybackSession.CanSeek) return;
        var session = _player.PlaybackSession;
        var wasPaused = session.PlaybackState != MediaPlaybackState.Playing;
        session.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, session.NaturalDuration.TotalSeconds));
        if (_grading && wasPaused) { _primeFrame = true; _player.IsMuted = true; _player.Play(); }
        UpdatePosition();
    }
    private static string Clock(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss") : time.ToString(@"mm\:ss");
    private void UpdatePosition()
    {
        if (_player == null) return;
        var session = _player.PlaybackSession;
        _updatingSeek = true;
        try
        {
            _seek.Maximum = Math.Max(1, session.NaturalDuration.TotalSeconds);
            _seek.Value = Math.Clamp(session.Position.TotalSeconds, 0, _seek.Maximum);
            _seek.IsEnabled = session.CanSeek;
            _time.Text = $"{Clock(session.Position)} / {Clock(session.NaturalDuration)}";
            UpdateChapterNavigation(session);
        }
        finally { _updatingSeek = false; }
    }

    private async Task LoadChaptersAsync(string path)
    {
        var load = new CancellationTokenSource();
        _chapterLoad = load;
        try
        {
            var chapters = await VideoChapters.ReadAsync(path, App.GetService<AppConfig>(), load.Token);
            if (!ReferenceEquals(_chapterLoad, load) || chapters.Count == 0) return;

            _chapters = chapters;
            _previousChapter.Visibility = _nextChapter.Visibility = _chapterMarkers.Visibility = Visibility.Visible;
            UpdatePosition();
            PositionChapterMarkers();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or
            System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidOperationException)
        {
            // Chapters are optional. A missing ffprobe or unreadable chapter list should not prevent playback.
        }
        finally
        {
            if (ReferenceEquals(_chapterLoad, load)) _chapterLoad = null;
            load.Dispose();
        }
    }

    private void UpdateChapterNavigation(MediaPlaybackSession session)
    {
        if (_chapters.Count == 0) return;
        var position = session.Position.TotalSeconds;
        var duration = session.NaturalDuration.TotalSeconds;
        _previousChapter.IsEnabled = session.CanSeek && Math.Min(_chapters[0].Seconds, duration) < position - ChapterSeekTolerance;
        _nextChapter.IsEnabled = session.CanSeek && Math.Min(_chapters[^1].Seconds, duration) > position + ChapterSeekTolerance;
        if (_chapterDuration == duration) return;
        _chapterDuration = duration;
        PositionChapterMarkers();
    }

    private void PositionChapterMarkers()
    {
        _chapterMarkers.Children.Clear();
        if (_chapterDuration <= 0 || _chapterMarkers.ActualWidth <= 0 || _chapters.Count == 0) return;

        var width = Math.Min(ChapterTargetSize, _chapterMarkers.ActualWidth);
        var span = _chapterMarkers.ActualWidth - width;
        var group = new List<PlaybackChapter>();
        var groupLeft = 0d;
        foreach (var chapter in _chapters)
        {
            var left = span * Math.Clamp(chapter.Seconds / _chapterDuration, 0, 1);
            if (group.Count > 0 && left - groupLeft >= width)
            {
                AddChapterMarker(group, groupLeft, width);
                group.Clear();
            }
            if (group.Count == 0) groupLeft = left;
            group.Add(chapter);
        }
        AddChapterMarker(group, groupLeft, width);
    }

    private void AddChapterMarker(List<PlaybackChapter> group, double left, double width)
    {
        var first = group[0];
        var accent = (Brush)Application.Current.Resources["ChapterMarkerBrush"];
        var marker = new Button
        {
            Width = width, Height = ChapterTargetSize, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        string label;
        if (group.Count == 1)
        {
            marker.Content = new Border { Width = 5, Height = 9, CornerRadius = new CornerRadius(2), Background = accent };
            label = ChapterLabel(first);
            marker.Click += (_, _) => SeekTo(first.Seconds);
        }
        else
        {
            marker.Content = new TextBlock { Text = group.Count.ToString(), FontSize = 11, Foreground = accent };
            label = $"{Clock(TimeSpan.FromSeconds(first.Seconds))} · {group.Count} 个事件";
            var flyout = new MenuFlyout();
            foreach (var chapter in group)
            {
                var item = new MenuFlyoutItem { Text = ChapterLabel(chapter) };
                item.Click += (_, _) => SeekTo(chapter.Seconds);
                flyout.Items.Add(item);
            }
            marker.Flyout = flyout;
        }

        var tooltip = new ToolTip { Content = label };
        ToolTipService.SetToolTip(marker, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(marker, label);
        marker.GotFocus += (_, _) => { if (marker.FocusState == FocusState.Keyboard) tooltip.IsOpen = true; };
        marker.LostFocus += (_, _) => tooltip.IsOpen = false;
        Canvas.SetLeft(marker, left);
        _chapterMarkers.Children.Add(marker);
    }

    private static string ChapterLabel(PlaybackChapter chapter) => $"{Clock(TimeSpan.FromSeconds(chapter.Seconds))} · {chapter.Title}";

    private void SeekChapter(bool forward)
    {
        var position = Position.TotalSeconds;
        var chapter = forward
            ? _chapters.FirstOrDefault(item => Math.Min(item.Seconds, _chapterDuration) > position + ChapterSeekTolerance)
            : _chapters.LastOrDefault(item => Math.Min(item.Seconds, _chapterDuration) < position - ChapterSeekTolerance);
        if (chapter != null) SeekTo(chapter.Seconds);
    }

    private void ShowAudioTracks()
    {
        if (_item == null) return;
        var item = _item;
        var flyout = new MenuFlyout();
        for (var i = 0; i < item.AudioTracks.Count; i++)
        {
            var index = i;
            var label = item.AudioTracks[i].Label;
            var menu = new ToggleMenuFlyoutItem { Text = string.IsNullOrWhiteSpace(label) ? $"音轨 {i + 1}" : label, IsChecked = item.AudioTracks.SelectedIndex == i };
            menu.Click += (_, _) => { if (ReferenceEquals(item, _item)) item.AudioTracks.SelectedIndex = index; };
            flyout.Items.Add(menu);
        }
        flyout.ShowAt(_tracks);
    }

    private void FrameAvailable(MediaPlayer sender, object args)
    {
        // Bound the queue to one GPU copy; decoding threads never touch XAML or disposed surfaces.
        if (Interlocked.Exchange(ref _queuedFrame, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _queuedFrame, 0);
            if (!ReferenceEquals(sender, _player) || _canvas == null) return;
            try
            {
                var session = sender.PlaybackSession;
                var width = (int)session.NaturalVideoWidth; var height = (int)session.NaturalVideoHeight;
                if (width <= 0 || height <= 0) return;
                if (_frame == null || _frame.SizeInPixels.Width != width || _frame.SizeInPixels.Height != height)
                {
                    _frame?.Dispose();
                    _frame = new CanvasRenderTarget(_canvas, width, height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Ignore);
                    _effect?.Dispose(); _effect = null;
                }
                sender.CopyFrameToVideoSurface(_frame);
                if (_primeFrame) { _primeFrame = false; sender.Pause(); sender.IsMuted = false; }
                _hasFrame = true; _canvas.Invalidate();
            }
            catch (Exception e) { FailFrame(e); }
        })) Interlocked.Exchange(ref _queuedFrame, 0);
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!_hasFrame || _frame == null) return;
        try
        {
            ICanvasImage image = _frame;
            if (_tone != null && _strength > 0)
            {
                if (_table == null)
                {
                    var rgb = ColorGradeLut.CreateRgb(_tone, _strength);
                    var colors = new Windows.UI.Color[rgb.Length / 3];
                    for (var i = 0; i < colors.Length; i++) colors[i] = Windows.UI.Color.FromArgb(255, rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
                    _table = EffectTransferTable3D.CreateFromColors(sender, colors, ColorGradeLut.Size, ColorGradeLut.Size, ColorGradeLut.Size);
                }
                _effect ??= new TableTransfer3DEffect { Source = _frame, Table = _table };
                image = _effect;
            }
            var scale = Math.Min(sender.ActualWidth / _frame.Size.Width, sender.ActualHeight / _frame.Size.Height);
            if (scale <= 0) return;
            args.DrawingSession.DrawImage(image, new Rect((sender.ActualWidth - _frame.Size.Width * scale) / 2,
                (sender.ActualHeight - _frame.Size.Height * scale) / 2, _frame.Size.Width * scale, _frame.Size.Height * scale),
                new Rect(0, 0, _frame.Size.Width, _frame.Size.Height));
        }
        catch (Exception e) { FailFrame(e); }
    }
    private void FailFrame(Exception e)
    {
        _player?.Pause(); ReleaseGpu();
        PlaybackFailed?.Invoke("实时预览不可用：" + e.Message);
    }
    private void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => DispatcherQueue.TryEnqueue(() =>
    { if (ReferenceEquals(sender, _player)) PlaybackFailed?.Invoke(args.ErrorMessage); });
    private void ReleaseGpu()
    {
        _hasFrame = false;
        _effect?.Dispose(); _effect = null;
        _table?.Dispose(); _table = null;
        _frame?.Dispose(); _frame = null;
    }
    public void Release()
    {
        _positionTimer.Stop();
        var chapterLoad = _chapterLoad;
        _chapterLoad = null;
        chapterLoad?.Cancel();
        _chapterMarkers.Children.Clear();
        _chapters = [];
        _chapterDuration = 0;
        _previousChapter.Visibility = _nextChapter.Visibility = _chapterMarkers.Visibility = Visibility.Collapsed;
        _bar.Opacity = _controlsBelow ? 1 : 0;
        _pointerOverBar = false;
        _primeFrame = false;
        _startPosition = null;
        _video.SetMediaPlayer(null);
        if (_player != null)
        {
            _player.MediaFailed -= Failed; _player.MediaOpened -= Opened;
            _player.VideoFrameAvailable -= FrameAvailable;
            _player.PlaybackSession.PlaybackStateChanged -= StateChanged;
            _player.Pause(); _player.Source = null; _player.Dispose(); _player = null;
        }
        _item = null; _source?.Dispose(); _source = null;
        ReleaseGpu(); _canvas?.Invalidate();
        _updatingSeek = true; _seek.Value = 0; _seek.Maximum = 1; _time.Text = "00:00 / 00:00"; _updatingSeek = false;
        ((FontIcon)_play.Content).Glyph = "\uE768";
        PlaybackChanged?.Invoke(false);
    }
}
