using System.Runtime.InteropServices;
using LolHighlight.Desktop.Controls;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition;
using Windows.Graphics;

namespace LolHighlight.Desktop.Services;

/// <summary>A reusable, non-activating notification window excluded from WGC before it is shown.</summary>
public sealed class EventNotificationService : IDisposable
{
    private readonly IRecordingSessionService _session;
    private readonly DispatcherQueue _dispatcher;
    private Window? _window;
    private AppWindow? _appWindow;
    private readonly AppConfig _config;
    private EventNotificationView? _view;
    private NotificationWindowChrome? _chrome;
    private DispatcherQueueTimer? _timer;
    private bool _closing;
    private bool _disposed;
    private bool _previewing;
    public string? LastError { get; private set; }

    public EventNotificationService(IRecordingSessionService session, DispatcherQueue dispatcher, AppConfig config)
    {
        _session = session;
        _dispatcher = dispatcher;
        _config = config;
        session.HighlightDetected += OnHighlight;
        session.StatusChanged += OnStatus;
    }

    private void OnHighlight(object? sender, HighlightDetectedEventArgs args)
        => _dispatcher.TryEnqueue(() => Show(args, _config.EventNotificationStyle));

    public bool Preview(string style) => Show(new(new HighlightTrigger(-1, "preview", "双杀", 0, DateTime.UtcNow, new()), false), style);

    private void OnStatus(object? sender, RecordingStatusChangedEventArgs args)
    {
        if (args.Snapshot.Status is not RecordingStatus.Recording)
            _dispatcher.TryEnqueue(() => { if (!_disposed && !_previewing) HideImmediately(); });
    }

    private bool Show(HighlightDetectedEventArgs args, string style)
    {
        if (_disposed) return false;
        try
        {
            LastError = null;
            EnsureWindow();
            if (_appWindow == null) return false;
            _timer!.Stop();
            _view?.StopAnimations();
            var detail = args.Trigger.Kind == "preview" ? "样式预览 · 不触发录像保存" :
                args.FullGame ? "整局录制中 · 此提示不进入录像" : "已标记高光 · 片段结束后保存";
            _view = new EventNotificationView(EventNotificationStyles.IsSupported(style) ? style : EventNotificationStyles.Default,
                args.Trigger.Label, detail);
            _window!.Content = _view;
            _previewing = args.Trigger.Kind == "preview";
            var target = new GameWindowLocator().FindLeagueWindow();
            var display = target == null ? DisplayArea.Primary :
                DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow((nint)target.WindowHandle), DisplayAreaFallback.Primary);
            var bounds = display.OuterBounds;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var scale = Math.Max(1, GetDpiForWindow(hwnd) / 96d);
            var width = Math.Min(bounds.Width, (int)Math.Ceiling(EventNotificationView.LogicalWidth * scale));
            var height = Math.Min(bounds.Height, (int)Math.Ceiling(EventNotificationView.LogicalHeight * scale));
            var margin = (int)Math.Round(10 * scale);
            _appWindow.MoveAndResize(new RectInt32(bounds.X + Math.Max(0, bounds.Width - width - margin),
                bounds.Y + Math.Min(margin, Math.Max(0, bounds.Height - height)), width, height));
            _appWindow.Show(false);
            _closing = false;
            _timer.Interval = TimeSpan.FromMilliseconds(3640);
            _timer.Start();
            return true;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            ResetWindow();
            RuntimeDiagnostics.Write(new AppPaths().ResolveRuntimeDirectory(".runtime"), $"事件提示不可用，录制继续：{error}");
            return false;
        }
    }

    private void EnsureWindow()
    {
        if (_window != null) return;
        // SystemBackdrop uses Windows.UI.Composition, which needs the separate system
        // queue even though WinUI's Microsoft.UI.Dispatching queue already exists.
        _dispatcher.EnsureSystemDispatcherQueue();
        _window = new Window
        {
            Title = "Rift Replay · 高光提示"
        };
        _window.SystemBackdrop = new TransparentNotificationBackdrop();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.IsShownInSwitchers = false;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        _chrome = new NotificationWindowChrome(hwnd);
        // Fail closed: if exclusion cannot be established, never display the popup.
        if (!CaptureExclusion.TryEnable(hwnd))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法排除提示窗口的画面采集");
        _timer = _dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += Hide;
    }

    private void Hide(DispatcherQueueTimer sender, object args)
    {
        if (_closing) { HideImmediately(); return; }
        try
        {
            _closing = true;
            _view?.PlayExit();
            sender.Interval = TimeSpan.FromMilliseconds(360);
            sender.Start();
        }
        catch { HideImmediately(); }
    }

    private void HideImmediately()
    {
        _timer?.Stop();
        _view?.StopAnimations();
        _appWindow?.Hide();
        _closing = false;
        _previewing = false;
    }

    private void ResetWindow()
    {
        HideImmediately();
        if (_timer != null) _timer.Tick -= Hide;
        _timer = null;
        _chrome?.Dispose();
        _chrome = null;
        _window?.Close();
        _window = null;
        _appWindow = null;
        _view = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.HighlightDetected -= OnHighlight;
        _session.StatusChanged -= OnStatus;
        ResetWindow();
    }

    // A transparent system backdrop lets the cut corners and capsule reveal the game,
    // including while the XAML content slides or fades. It does not sample game pixels.
    private sealed class TransparentNotificationBackdrop : SystemBackdrop
    {
        private Windows.UI.Composition.Compositor? _compositor;
        private Windows.UI.Composition.CompositionColorBrush? _brush;

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            _compositor = new Windows.UI.Composition.Compositor();
            _brush = _compositor.CreateColorBrush(Colors.Transparent);
            connectedTarget.SystemBackdrop = _brush;
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            disconnectedTarget.SystemBackdrop = null;
            _brush?.Dispose();
            _brush = null;
            _compositor?.Dispose();
            _compositor = null;
            base.OnTargetDisconnected(disconnectedTarget);
        }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
