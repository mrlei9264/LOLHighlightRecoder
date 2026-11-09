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
    private bool _active, _subscribed, _confirmingDelete;
    private bool? _playlistExpanded;
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
        UpdateLayoutMode();
        if (Visibility == Visibility.Visible) SetActive(true);
    }
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (!active) StopPlayback();
        else _ = _viewModel.RefreshAsync();
    }
    public void Refresh() => _ = _viewModel.RefreshAsync();
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryViewModel.CountText)) return;
        UpdateLayoutMode();
        if (_playingPath != null && _viewModel.FindCard(_playingPath) is { } card) UpdatePlayingDetails(card);
        else if (_playingPath != null) PlaybackList.SelectedItem = null;
    }
    private void ViewMode_Click(object sender, RoutedEventArgs e) => UpdateLayoutMode();
    private void ClearFilters_Click(object sender, RoutedEventArgs e) => _viewModel.ResetFilters();
    private void SelectAll_Click(object sender, RoutedEventArgs e) => _viewModel.MarkVisible(true);
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => _viewModel.MarkVisible(false);
    private void GradeSelected_Click(object sender, RoutedEventArgs e) => OpenGrading(_viewModel.Cards.Where(card => card.IsMarked).Select(card => card.Path));
    private async void DeleteMarked_Click(object sender, RoutedEventArgs e) => await DeleteAsync(_viewModel.Cards.Where(card => card.IsMarked).ToArray());
    private void Play_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: LibraryVideoCard card }) StartPlayback(card); }
    private void PlaybackList_ItemClick(object sender, ItemClickEventArgs e)
    { if (e.ClickedItem is LibraryVideoCard card) StartPlayback(card); }
    private void StartPlayback(LibraryVideoCard card, bool autoPlay = true, TimeSpan? position = null)
    {
        if (!_active) return;
        _viewModel.SelectedCard = card;
        _viewModel.MultiSelect = false;
        _playingPath = card.Path;
        UpdateLayoutMode();
        UpdatePlayingDetails(card);
        PreviewPlayer.Open(card.Path, autoPlay, position);
        PreviewPlayer.FocusPlayer();
    }
    private void UpdatePlayingDetails(LibraryVideoCard card)
    {
        PlaybackList.SelectedItem = _viewModel.Cards.FirstOrDefault(item => string.Equals(item.Path, card.Path, StringComparison.OrdinalIgnoreCase));
        PlayingTitle.Text = card.Title;
        PlayingTags.Text = card.TagText;
    }
    private void UpdateLayoutMode()
    {
        var browsing = _playingPath == null;
        var list = ListMode.IsChecked == true;
        BrowseToolbar.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
        VideoRowsPanel.Visibility = browsing && list ? Visibility.Visible : Visibility.Collapsed;
        VideoList.Visibility = browsing && !list ? Visibility.Visible : Visibility.Collapsed;
        EmptyLibrary.Visibility = browsing && !_viewModel.HasVideos ? Visibility.Visible : Visibility.Collapsed;
        PlaybackLayout.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
        PlaybackEmpty.Visibility = !browsing && !_viewModel.HasVideos ? Visibility.Visible : Visibility.Collapsed;
        if (!browsing) UpdatePlaybackSidebar();
        if (browsing && !list) UpdateCardSize();
    }
    private void StopPlayback()
    {
        if (PreviewPlayer.IsFullScreen && (Application.Current as App)?.MainWindow is MainWindow window) window.TogglePlayerFullScreen(PreviewPlayer);
        PreviewPlayer.Release();
        _playingPath = null;
        _playlistExpanded = null;
        UpdateLayoutMode();
    }
    private void Back_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void PlaybackLayout_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlaybackSidebar();
    private void PlaylistToggle_Click(object sender, RoutedEventArgs e)
    {
        _playlistExpanded = PlaybackSidebar.Visibility != Visibility.Visible;
        UpdatePlaybackSidebar();
    }
    private void UpdatePlaybackSidebar()
    {
        if (_playingPath == null) return;
        var expanded = _playlistExpanded ?? PlaybackLayout.ActualWidth >= 720;
        PlaybackSidebar.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        PlaybackSidebarColumn.MinWidth = expanded ? 230 : 0;
        PlaybackSidebarColumn.Width = expanded ? new GridLength(27, GridUnitType.Star) : new GridLength(0);
        PlaybackLayout.ColumnSpacing = expanded ? 14 : 0;
        PlaylistToggle.Content = expanded ? "收起列表" : "播放列表";
    }
    private void VideoMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryVideoCard card } target) CreateVideoMenu(card).ShowAt(target);
    }

    private void Video_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryVideoCard card } target) return;
        CreateVideoMenu(card).ShowAt(target, new FlyoutShowOptions { Position = e.GetPosition(target) });
        e.Handled = true;
    }

    private MenuFlyout CreateVideoMenu(LibraryVideoCard card)
    {
        var menu = new MenuFlyout();
        void Item(string label, Action action)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item("播放", () => StartPlayback(card));
        Item("调色", () => OpenGrading([card.Path]));
        Item("修改库内标题", () => _ = RenameAsync(card, false));
        Item("修改文件名", () => _ = RenameAsync(card, true));
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("外部播放器打开", () => ExecuteForCard(card, _viewModel.PlayCommand));
        Item("在文件夹中显示", () => ExecuteForCard(card, _viewModel.RevealCommand));
        Item("导出对局数据", () => ExecuteForCard(card, _viewModel.ExportTelemetryCommand));
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("删除录像", () => _ = DeleteAsync([card]));
        return menu;
    }
    private void OpenGrading(IEnumerable<string> paths)
    {
        if ((Application.Current as App)?.MainWindow is MainWindow window && !window.OpenGrading(paths))
            _viewModel.ReportGradingBusy();
    }
    private void ExecuteForCard(LibraryVideoCard card, System.Windows.Input.ICommand command)
    {
        _viewModel.SelectedCard = card;
        command.Execute(null);
    }
    private async Task DeleteAsync(LibraryVideoCard[] cards)
    {
        if (cards.Length == 0 || _confirmingDelete || !_active) return;
        _confirmingDelete = true;
        try
        {
            var recycle = _viewModel.DeleteToRecycleBin;
            var files = cards.Length == 1 ? cards[0].FileName : $"所选的 {cards.Length} 个录像文件";
            var consequence = recycle ? "可从 Windows 回收站恢复。" : "此操作无法撤销。";
            var action = recycle ? "移入回收站" : "永久删除";
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = recycle ? "将录像移入回收站？" : "永久删除录像？",
                Content = new TextBlock { Text = $"{files}{Environment.NewLine}{Environment.NewLine}{consequence}", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = action,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && _active)
                DeleteConfirmed(cards, recycle);
        }
        finally { _confirmingDelete = false; }
    }

    private void DeleteConfirmed(LibraryVideoCard[] cards, bool recycle)
    {
        var current = cards.FirstOrDefault(card => string.Equals(card.Path, _playingPath, StringComparison.OrdinalIgnoreCase));
        var position = PreviewPlayer.Position;
        var wasPlaying = PreviewPlayer.IsPlaying;
        var next = current == null ? [] : _viewModel.Cards
            .SkipWhile(card => !string.Equals(card.Path, current.Path, StringComparison.OrdinalIgnoreCase))
            .Skip(1).Select(card => card.Path).ToArray();
        if (current != null) PreviewPlayer.Release();
        var removed = _viewModel.DeleteVideos(cards.Select(card => card.Video), recycle);
        if (current == null) return;
        if (!removed.Contains(current.Path))
        {
            StartPlayback(current, wasPlaying, position);
            return;
        }
        var nextCard = next.Where(path => !removed.Contains(path)).Select(_viewModel.FindCard).FirstOrDefault(card => card != null);
        if (nextCard != null) StartPlayback(nextCard);
        else StopPlayback();
    }

    private async Task RenameAsync(LibraryVideoCard card, bool renameFile)
    {
        var input = new TextBox
        {
            Header = renameFile ? "文件名（保留 .mp4 扩展名）" : "显示标题（留空恢复默认标题）",
            Text = renameFile ? Path.GetFileNameWithoutExtension(card.Path) : card.Title
        };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = renameFile ? "修改文件名" : "修改库内标题", Content = input,
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !_active) return;
        if (!renameFile) { _viewModel.RenameTitle(card, input.Text); return; }

        var current = string.Equals(card.Path, _playingPath, StringComparison.OrdinalIgnoreCase);
        var position = PreviewPlayer.Position;
        var wasPlaying = PreviewPlayer.IsPlaying;
        if (current) PreviewPlayer.Release();
        var path = _viewModel.RenameFile(card, input.Text);
        if (current) StartPlayback(path == null ? card : _viewModel.FindCard(path) ?? card, wasPlaying, position);
    }
    private void Gallery_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardSize();
    private void UpdateCardSize()
    {
        if (VideoList.ItemsPanelRoot is not ItemsWrapGrid panel || VideoList.ActualWidth <= 0) return;
        var available = Math.Max(1, VideoList.ActualWidth - 16);
        var columns = Math.Max(1, (int)(available / 232));
        panel.ItemWidth = available / columns;
        panel.ItemHeight = (panel.ItemWidth - 10) * 9 / 16 + 86;
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

