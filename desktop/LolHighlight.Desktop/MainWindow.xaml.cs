using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using LolHighlight.Recording.Services;
using LolHighlight.Desktop.ViewModels;

namespace LolHighlight.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;
    private bool _allowClose;
    private bool _closing;
    private Panel? _playerParent;
    private int _playerIndex;
    private LolHighlight.Desktop.Controls.MediaPreview? _fullScreenPlayer;
    private AppWindowPresenter? _previousPresenter;
    private LolHighlight.Desktop.Services.MatchReviewCoordinator? _matchReview;

    public MainWindow()
    {
        InitializeComponent();

        var viewModel = App.GetService<MainViewModel>();
        RootGrid.DataContext = viewModel;
        LibraryPageControl.PlaybackChanged += playing => viewModel.SetPlaybackActivity(true, playing);
        GradingPageControl.PlaybackChanged += playing => viewModel.SetPlaybackActivity(false, playing);
        GradingPageControl.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GradingViewModel.IsBusy) or nameof(GradingViewModel.Progress))
                viewModel.SetGradingActivity(GradingPageControl.ViewModel.IsBusy, GradingPageControl.ViewModel.Progress);
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Closing += AppWindow_Closing;

        ConfigureWindow(windowId);
        try { Navigation.IsPaneOpen = !System.Text.Json.JsonSerializer.Deserialize<bool>(File.ReadAllText(NavigationPreferencesPath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        Navigation.PaneOpened += (_, _) => SaveNavigationPreference();
        Navigation.PaneClosed += (_, _) => SaveNavigationPreference();
        RootGrid.SizeChanged += (_, _) => ResizeFullScreenHost();
        Navigation.SelectedItem = NavHome;
        _matchReview = new LolHighlight.Desktop.Services.MatchReviewCoordinator(this, App.GetService<MatchReviewStore>(), () =>
        { LibraryPageControl.Refresh(); _ = GradingPageControl.ViewModel.RefreshAsync(); }, UpdatePendingReview);
    }

    private void ReviewPending_Click(object sender, RoutedEventArgs args) => ReviewPending();
    private void ReviewDiscarded_Click(object sender, RoutedEventArgs args) => _matchReview?.ReviewDiscarded();

    internal void ReviewPending() => _matchReview?.ReviewPending();

    private void UpdatePendingReview(int count)
    {
        var label = count > 0 ? $"待确认 · {count} 场" : "待确认录像";
        PendingReviewLabel.Text = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PendingReviewShortcut, label);
        ToolTipService.SetToolTip(PendingReviewShortcut, label);
        HomePageControl.SetPendingReviewCount(count);
    }

    private void ConfigureWindow(WindowId windowId)
    {
        Title = "Rift Replay";
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "RiftReplay.ico");
        if (File.Exists(icon)) _appWindow.SetIcon(icon);

        var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var width = Math.Min(1280, workArea.Width);
        var height = Math.Min(720, workArea.Height);
        var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min(760, workArea.Width);
            presenter.PreferredMinimumHeight = Math.Min(560, workArea.Height);
            RootGrid.Loaded += (_, _) =>
            {
                var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
                presenter.PreferredMinimumWidth = Math.Min((int)Math.Ceiling(760 * scale), width);
                presenter.PreferredMinimumHeight = Math.Min((int)Math.Ceiling(560 * scale), height);
            };
        }

        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        var titleBar = _appWindow.TitleBar;
        titleBar.BackgroundColor = HexColor("#202020");
        titleBar.ForegroundColor = HexColor("#F5F5F5");
        titleBar.InactiveBackgroundColor = HexColor("#202020");
        titleBar.InactiveForegroundColor = HexColor("#A0A0A0");
        titleBar.ButtonBackgroundColor = HexColor("#202020");
        titleBar.ButtonForegroundColor = HexColor("#F5F5F5");
        titleBar.ButtonHoverBackgroundColor = HexColor("#333333");
        titleBar.ButtonHoverForegroundColor = HexColor("#FFFFFF");
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
            return;

        args.Cancel = true;
        if (_closing)
            return;

        _closing = true;
        try
        {
            if (_matchReview != null) await _matchReview.StopAsync();
            await HomePageControl.StopAsync();
            await GradingPageControl.StopAsync();
            await App.GetService<IRecordingSessionService>().StopAsync();
            SettingsPageControl.FlushAutoSave();
        }
        catch
        {
        }

        _allowClose = true;
        Close();
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string page })
            return;

        if (HomePageControl == null)
            return;

        HomePageControl.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPageControl.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPageControl.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        GradingPageControl.Visibility = page == "grading" ? Visibility.Visible : Visibility.Collapsed;
        RecordingShortcut.Visibility = page == "home" ? Visibility.Collapsed : Visibility.Visible;

        if (RootGrid.DataContext is MainViewModel viewModel)
            viewModel.CurrentPage = page;

        LibraryPageControl.SetActive(page == "library");
        HomePageControl.SetActive(page == "home");
        GradingPageControl.SetActive(page == "grading");
    }

    public bool OpenGrading(IEnumerable<string> sources)
    {
        if (GradingPageControl.ViewModel.IsBusy) return false;
        GradingPageControl.ViewModel.AddSources(sources);
        Navigation.SelectedItem = NavGrading;
        return true;
    }

    private static string NavigationPreferencesPath => Path.Combine(App.GetService<AppPaths>().DataRoot, "navigation.json");

    private void SaveNavigationPreference()
    {
        if (Navigation.DisplayMode != NavigationViewDisplayMode.Expanded) return;
        try
        {
            var dataRoot = App.GetService<AppPaths>().DataRoot;
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(NavigationPreferencesPath, System.Text.Json.JsonSerializer.Serialize(!Navigation.IsPaneOpen));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private void RecordingToggle_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.DataContext is MainViewModel { CanStop: true } vm) vm.StopCommand.Execute(null);
        else StartRecording();
    }

    public void TogglePlayerFullScreen(LolHighlight.Desktop.Controls.MediaPreview player)
    {
        if (_fullScreenPlayer != null)
        {
            if (!ReferenceEquals(_fullScreenPlayer, player)) return;
            var previous = _fullScreenPlayer;
            FullScreenHost.Children.Remove(previous);
            FullScreenPopup.IsOpen = false;
            _playerParent?.Children.Insert(Math.Min(_playerIndex, _playerParent.Children.Count), previous);
            previous.IsFullScreen = false;
            _fullScreenPlayer = null;
            if (_previousPresenter != null) _appWindow.SetPresenter(_previousPresenter);
            _playerParent = null;
            previous.FocusPlayer();
            return;
        }
        if (player.Parent is not Panel parent) return;
        _playerParent = parent; _playerIndex = parent.Children.IndexOf(player);
        _previousPresenter = _appWindow.Presenter;
        parent.Children.Remove(player);
        _fullScreenPlayer = player; player.IsFullScreen = true;
        FullScreenHost.Children.Add(player);
        _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        ResizeFullScreenHost();
        FullScreenPopup.IsOpen = true;
        player.FocusPlayer();
    }

    private void ResizeFullScreenHost()
    {
        FullScreenHost.Width = RootGrid.ActualWidth;
        FullScreenHost.Height = RootGrid.ActualHeight;
    }

    public void StartRecording()
    {
        if (!SettingsPageControl.FlushAutoSave()) { Navigation.SelectedItem = NavSettings; return; }
        if (RootGrid.DataContext is MainViewModel vm && vm.CanStart) vm.StartCommand.Execute(null);
    }
    private static Windows.UI.Color HexColor(string hex) => Windows.UI.Color.FromArgb(
        255,
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));
}
