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
    private bool _navigationCollapsed;
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
        try { _navigationCollapsed = System.Text.Json.JsonSerializer.Deserialize<bool>(File.ReadAllText(Path.Combine(App.GetService<AppPaths>().DataRoot, "navigation.json"))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        UpdateNavigation();
        RootGrid.KeyDown += (_, e) => { if (_fullScreenPlayer != null && !e.Handled) e.Handled = _fullScreenPlayer.HandleKey(e.Key); };
        NavHome.IsChecked = true;
        _matchReview = new LolHighlight.Desktop.Services.MatchReviewCoordinator(this, App.GetService<MatchReviewStore>(), () =>
        { LibraryPageControl.Refresh(); _ = GradingPageControl.ViewModel.RefreshAsync(); });
    }

    private void ConfigureWindow(WindowId windowId)
    {
        Title = "Rift Replay · 录制与调色";
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "RiftReplay.ico");
        if (File.Exists(icon)) _appWindow.SetIcon(icon);

        var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var width = Math.Min(1500, workArea.Width);
        var height = Math.Min(960, workArea.Height);
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
                presenter.PreferredMinimumWidth = Math.Min((int)Math.Ceiling(760 * scale), workArea.Width);
                presenter.PreferredMinimumHeight = Math.Min((int)Math.Ceiling(560 * scale), workArea.Height);
            };
        }

        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        var titleBar = _appWindow.TitleBar;
        titleBar.BackgroundColor = HexColor("#0C0F12");
        titleBar.ForegroundColor = HexColor("#F3F6F1");
        titleBar.InactiveBackgroundColor = HexColor("#0C0F12");
        titleBar.InactiveForegroundColor = HexColor("#6F7A74");
        titleBar.ButtonBackgroundColor = HexColor("#0C0F12");
        titleBar.ButtonForegroundColor = HexColor("#A7B0AA");
        titleBar.ButtonHoverBackgroundColor = HexColor("#1D252B");
        titleBar.ButtonHoverForegroundColor = HexColor("#F3F6F1");
        titleBar.ButtonPressedBackgroundColor = HexColor("#171D22");
        titleBar.ButtonPressedForegroundColor = HexColor("#F3F6F1");
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

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string page })
            return;

        if (HomePageControl == null)
            return;

        HomePageControl.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPageControl.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPageControl.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        GradingPageControl.Visibility = page == "grading" ? Visibility.Visible : Visibility.Collapsed;

        if (RootGrid.DataContext is MainViewModel viewModel)
            viewModel.CurrentPage = page;

        LibraryPageControl.SetActive(page == "library");
        HomePageControl.SetActive(page == "home");
        GradingPageControl.SetActive(page == "grading");
    }

    public void OpenGrading(IEnumerable<string> sources)
    {
        GradingPageControl.ViewModel.AddSources(sources);
        NavGrading.IsChecked = true;
    }

    private void CollapseNavigation_Click(object sender, RoutedEventArgs e)
    {
        _navigationCollapsed = !_navigationCollapsed; UpdateNavigation();
        try
        {
            var dataRoot = App.GetService<AppPaths>().DataRoot;
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot, "navigation.json"), System.Text.Json.JsonSerializer.Serialize(_navigationCollapsed));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private void UpdateNavigation()
    {
        SidebarColumn.MinWidth = _navigationCollapsed ? 64 : 168;
        SidebarColumn.Width = new GridLength(_navigationCollapsed ? 64 : 13, _navigationCollapsed ? GridUnitType.Pixel : GridUnitType.Star);
        var visibility = _navigationCollapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var element in new FrameworkElement[] { BrandCopy, HomeNavLabel, LibraryNavLabel, GradingNavLabel, SettingsNavLabel, RecordNavLabel, SidebarWorkStatus }) element.Visibility = visibility;
        Grid.SetColumn(CollapseNavigation, _navigationCollapsed ? 0 : 1);
        CollapseNavigation.HorizontalAlignment = _navigationCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        Grid.SetColumnSpan(SidebarStatusDot, _navigationCollapsed ? 2 : 1);
        SidebarStatusDot.HorizontalAlignment = HorizontalAlignment.Center;
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
            var previous = _fullScreenPlayer;
            FullScreenHost.Children.Remove(previous);
            _playerParent?.Children.Insert(Math.Min(_playerIndex, _playerParent.Children.Count), previous);
            FullScreenHost.Visibility = Visibility.Collapsed;
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
        FullScreenHost.Children.Add(player); FullScreenHost.Visibility = Visibility.Visible;
        _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        player.FocusPlayer();
    }

    public void StartRecording()
    {
        if (GradingPageControl.ViewModel.IsBusy) { GradingPageControl.ViewModel.ShowError("请先完成或取消调色，再开始录制。"); NavGrading.IsChecked = true; return; }
        if (!SettingsPageControl.FlushAutoSave()) { NavSettings.IsChecked = true; return; }
        if (RootGrid.DataContext is MainViewModel vm && vm.CanStart) vm.StartCommand.Execute(null);
    }
    private void GlobalStart_Click(object sender, RoutedEventArgs e) => StartRecording();

    private static Windows.UI.Color HexColor(string hex) => Windows.UI.Color.FromArgb(
        255,
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));
}
