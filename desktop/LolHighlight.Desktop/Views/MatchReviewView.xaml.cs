using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using LolHighlight.Desktop.Services;
using LolHighlight.Desktop.ViewModels;

namespace LolHighlight.Desktop.Views;

public sealed class MatchReviewVideo(string path, string title, string tags) : GradingObservable
{
    private bool _keep = true;
    public string Path { get; } = path;
    public string Title { get; } = title;
    public string Tags { get; } = tags;
    public bool Keep { get => _keep; set => Set(ref _keep, value); }
}

public sealed partial class MatchReviewView : UserControl
{
    private readonly HashSet<Task<string?>> _thumbnailJobs = new();
    private bool _released;
    public ObservableCollection<MatchReviewVideo> Videos { get; } = new();
    public event Action? SelectionChanged;
    public IEnumerable<string> SelectedPaths => Videos.Where(v => v.Keep).Select(v => v.Path).ToArray();
    public int KeepCount => Videos.Count(v => v.Keep);
    public string Error { set => ErrorText.Text = value; }
    public MatchReviewView()
    {
        InitializeComponent(); VideoCards.ItemsSource = Videos;
        Preview.PlaybackFailed += message => ErrorText.Text = message;
    }
    public async Task LoadAsync(IEnumerable<string> files)
    {
        var footer = App.GetService<IMetadataFooterService>();
        var videos = await Task.Run(() => files.Where(File.Exists).Select(file =>
            new MatchReviewVideo(file, $"{File.GetLastWriteTime(file):HH:mm} · {new FileInfo(file).Length / 1048576d:0.0} MB", VideoTags.Read(footer, file))).ToArray());
        foreach (var video in videos)
        {
            video.PropertyChanged += (_, _) => SelectionChanged?.Invoke();
            Videos.Add(video);
        }
        if (Videos.Count > 0) { VideoCards.SelectedItem = Videos[0]; ShowVideo(Videos[0], false); }
    }
    public void Release() { _released = true; Preview.Release(); }
    public async Task ReleaseAsync() { Release(); await Task.WhenAll(_thumbnailJobs.ToArray()); }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var video in Videos) video.Keep = true; }
    private void ClearAll_Click(object sender, RoutedEventArgs e) { foreach (var video in Videos) video.Keep = false; }
    private void Video_ItemClick(object sender, ItemClickEventArgs e)
    { if (e.ClickedItem is MatchReviewVideo video) ShowVideo(video, true); }
    private void ShowVideo(MatchReviewVideo video, bool play)
    { PreviewTitle.Text = video.Title + " · " + video.Tags; Preview.Open(video.Path, play); }
    private void Cards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (VideoCards.ItemsPanelRoot is not ItemsWrapGrid panel || VideoCards.ActualWidth <= 0) return;
        var available = Math.Max(1, VideoCards.ActualWidth - 16);
        var columns = Math.Max(1, (int)(available / 210));
        panel.ItemWidth = available / columns; panel.ItemHeight = (panel.ItemWidth - 24) * 9 / 16 + 84;
    }
    private async void Poster_Loaded(object sender, RoutedEventArgs e) { if (sender is Image image) await LoadPoster(image); }
    private async void Poster_Changed(FrameworkElement sender, DataContextChangedEventArgs e) { if (sender is Image { IsLoaded: true } image) await LoadPoster(image); }
    private async Task LoadPoster(Image image)
    {
        if (_released) return;
        image.Source = null; var version = new object(); image.Tag = version;
        if (image.DataContext is not MatchReviewVideo video) return;
        var job = App.GetService<VideoThumbnailService>().GetAsync(video.Path);
        _thumbnailJobs.Add(job);
        try
        {
            var file = await job;
            if (!_released && file != null && ReferenceEquals(image.Tag, version)) image.Source = new BitmapImage(new Uri(file));
        }
        finally { _thumbnailJobs.Remove(job); }
    }
    private void Poster_Unloaded(object sender, RoutedEventArgs e) { if (sender is Image image) { image.Tag = null; image.Source = null; } }
}

