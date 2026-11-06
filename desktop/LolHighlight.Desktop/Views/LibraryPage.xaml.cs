using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using LolHighlight.Desktop.ViewModels;

namespace LolHighlight.Desktop.Views;

public sealed partial class LibraryPage : UserControl
{
    private readonly LibraryViewModel _viewModel;
    private readonly LolHighlight.Desktop.Services.VideoThumbnailService _thumbnails;
    private bool _active, _subscribed, _playing, _updatingDates;
    private string? _playingPath;
    public event Action<bool>? PlaybackChanged;

    public LibraryPage()
    {
        InitializeComponent();
        _viewModel = App.GetService<LibraryViewModel>(); DataContext = _viewModel;
        _thumbnails = App.GetService<LolHighlight.Desktop.Services.VideoThumbnailService>();
        _viewModel.PropertyChanged += Changed; _subscribed = true;
        PreviewPlayer.PlaybackFailed += message => _viewModel.ReportPlaybackError(message);
        PreviewPlayer.PlaybackChanged += playing => PlaybackChanged?.Invoke(playing);
        PreviewPlayer.ExitRequested += StopPlayback;
        Loaded += Page_Loaded;
        Unloaded += (_, _) => { StopPlayback(); _active = false; _viewModel.PropertyChanged -= Changed; _subscribed = false; };
    }
    private void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (!_subscribed) { _viewModel.PropertyChanged += Changed; _subscribed = true; }
        UpdateDates();
        if (Visibility == Visibility.Visible) SetActive(true);
    }
    public void SetActive(bool active)
    {
        _active = active;
        if (!active) StopPlayback();
        else _ = _viewModel.RefreshAsync();
    }
    public void Refresh() => _ = _viewModel.RefreshAsync();
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.HasVideos)) EmptyLibrary.Visibility = !_playing && !_viewModel.HasVideos ? Visibility.Visible : Visibility.Collapsed;
        if (e.PropertyName is nameof(LibraryViewModel.LibraryDateMax) or nameof(LibraryViewModel.DateFrom) or nameof(LibraryViewModel.DateTo)) UpdateDates();
    }
    private void UpdateDates()
    {
        _updatingDates = true;
        try
        {
            foreach (var picker in new[] { FromDate, ToDate })
            {
                picker.Date = null;
                // Reset both bounds before narrowing them; DateChanged must not feed a clamped default back to the filter.
                picker.MinDate = new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);
                picker.MaxDate = new DateTimeOffset(9998, 12, 31, 0, 0, 0, TimeSpan.Zero);
                picker.MaxDate = _viewModel.LibraryDateMax;
                picker.MinDate = _viewModel.LibraryDateMin;
            }
            FromDate.Date = _viewModel.DateFrom;
            ToDate.Date = _viewModel.DateTo;
        }
        finally { _updatingDates = false; }
    }
    private void Date_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_updatingDates || _viewModel == null) return;
        if (ReferenceEquals(sender, FromDate)) _viewModel.DateFrom = args.NewDate;
        else _viewModel.DateTo = args.NewDate;
    }
    private void ClearFilters_Click(object sender, RoutedEventArgs e) => _viewModel.ResetFilters();
    private void Play_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: LibraryVideoCard card }) StartPlayback(card); }
    private void PlaybackList_ItemClick(object sender, ItemClickEventArgs e)
    { if (e.ClickedItem is LibraryVideoCard card) StartPlayback(card); }
    private void StartPlayback(LibraryVideoCard card)
    {
        if (!_active) return;
        _viewModel.SelectedCard = card; _playingPath = card.Path; _playing = true;
        VideoList.Visibility = EmptyLibrary.Visibility = Visibility.Collapsed;
        PlaybackLayout.Visibility = Visibility.Visible;
        PlayingTitle.Text = card.Title; PlayingTags.Text = card.TagText;
        PreviewPlayer.Open(card.Path, true); PreviewPlayer.FocusPlayer();
    }
    private void StopPlayback()
    {
        if (PreviewPlayer.IsFullScreen && (Application.Current as App)?.MainWindow is MainWindow window) window.TogglePlayerFullScreen(PreviewPlayer);
        PreviewPlayer.Release(); _playing = false; _playingPath = null;
        PlaybackLayout.Visibility = Visibility.Collapsed; VideoList.Visibility = Visibility.Visible;
        EmptyLibrary.Visibility = _viewModel.HasVideos ? Visibility.Collapsed : Visibility.Visible;
        UpdateCardSize(); PlaybackChanged?.Invoke(false);
    }
    private void Back_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void Card_Entered(object sender, PointerRoutedEventArgs e)
    { if (sender is Grid card && card.FindName("PlayOverlay") is Button button) button.Opacity = 1; }
    private void Card_Exited(object sender, PointerRoutedEventArgs e)
    { if (sender is Grid card && card.FindName("PlayOverlay") is Button button) button.Opacity = 0; }

    private void Video_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryVideoCard card } target) return;
        _viewModel.SelectedCard = card;
        var menu = new MenuFlyout();
        void Item(string label, Action action)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => { _viewModel.SelectedCard = card; action(); };
            menu.Items.Add(item);
        }
        Item("播放", () => StartPlayback(card));
        Item("调色", () => { if ((Application.Current as App)?.MainWindow is MainWindow window) window.OpenGrading([card.Path]); });
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("外部播放器打开", () => _viewModel.PlayCommand.Execute(null));
        Item("在文件夹中显示", () => _viewModel.RevealCommand.Execute(null));
        Item("导出对局数据", () => _viewModel.ExportTelemetryCommand.Execute(null));
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("删除录像", () => _ = DeleteAsync(card));
        menu.ShowAt(target, new FlyoutShowOptions { Position = e.GetPosition(target) });
        e.Handled = true;
    }
    private async Task DeleteAsync(LibraryVideoCard card)
    {
        var resume = _playing && !string.Equals(_playingPath, card.Path, StringComparison.OrdinalIgnoreCase);
        if (!resume) StopPlayback();
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "删除录像？", Content = card.Video.Name,
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        { _viewModel.SelectedCard = card; _viewModel.DeleteSelected(); }
    }
    private void Gallery_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardSize();
    private void UpdateCardSize()
    {
        if (VideoList.ItemsPanelRoot is not ItemsWrapGrid panel || VideoList.ActualWidth <= 0) return;
        var available = Math.Max(1, VideoList.ActualWidth - 16);
        var columns = Math.Max(1, (int)(available / 250));
        panel.ItemWidth = available / columns;
        panel.ItemHeight = (panel.ItemWidth - 24) * 9 / 16 + 100;
    }
    private async void Poster_Loaded(object sender, RoutedEventArgs e)
    { UpdateCardSize(); if (sender is Image image) await LoadPosterAsync(image); }
    private async void Poster_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    { if (sender is Image image && image.IsLoaded) await LoadPosterAsync(image); }
    private async Task LoadPosterAsync(Image image)
    {
        image.Source = null;
        var token = new object(); image.Tag = token;
        if (image.DataContext is not LibraryVideoCard card) return;
        var path = await _thumbnails.GetAsync(card.Path);
        if (path != null && ReferenceEquals(image.Tag, token) && image.DataContext is LibraryVideoCard current && current.Path == card.Path)
            image.Source = new BitmapImage(new Uri(path));
    }
    private void Poster_Unloaded(object sender, RoutedEventArgs e)
    { if (sender is Image image) { image.Tag = null; image.Source = null; } }
    private void StatusInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => _viewModel.ClearStatus();
}

