using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using LolHighlight.Desktop.Views;

namespace LolHighlight.Desktop.Services;

internal sealed class MatchReviewCoordinator
{
    private readonly Window _owner;
    private readonly MatchReviewStore _store;
    private readonly DispatcherQueueTimer _timer;
    private readonly Action _saved;
    private bool _showing, _disposed;
    private ContentDialog? _dialog;
    private Task _work = Task.CompletedTask;
    private DateTime _retryAfter;
    private string _error = "";

    public MatchReviewCoordinator(Window owner, MatchReviewStore store, Action saved)
    {
        _owner = owner; _store = store; _saved = saved;
        _timer = owner.DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => TryShow(); _timer.Start();
        store.Ready += Ready;
    }
    private void Ready(object? sender, EventArgs e) => _owner.DispatcherQueue.TryEnqueue(TryShow);
    private void TryShow()
    {
        if (_disposed || _showing || DateTime.UtcNow < _retryAfter || _owner.Content is not FrameworkElement { IsLoaded: true, XamlRoot: { } root }) return;
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0) return;
        _showing = true; _work = ShowAsync(root);
    }
    private async Task ShowAsync(XamlRoot root)
    {
        MatchReviewView? view = null;
        try
        {
            var batch = (await Task.Run(_store.Pending)).FirstOrDefault();
            if (_disposed || batch == null) return;
            var files = _store.Files(batch);
            var width = Math.Max(320, Math.Min(1080, root.Size.Width - 48));
            view = new MatchReviewView { Width = Math.Max(280, width - 48), Height = Math.Max(220, Math.Min(570, root.Size.Height - 210)) };
            await view.LoadAsync(files);
            if (_disposed) return;
            view.Error = _error;
            _dialog = new ContentDialog { XamlRoot = root, Title = $"选择本局录像 · {batch.CreatedAt:MM-dd HH:mm}", Content = view,
                PrimaryButtonText = $"保存所选 ({view.KeepCount})", CloseButtonText = "全部保留", DefaultButton = ContentDialogButton.None };
            _dialog.Resources["ContentDialogMaxWidth"] = width;
            _dialog.Resources["ContentDialogMaxHeight"] = Math.Max(300, root.Size.Height - 24);
            view.SelectionChanged += () => { if (_dialog != null) _dialog.PrimaryButtonText = view.KeepCount == 0 ? "不保留任何录像" : $"保存所选 ({view.KeepCount})"; };
            _owner.Activate();
            var result = await _dialog.ShowAsync();
            await view.ReleaseAsync();
            if (_disposed) return;
            var keep = result == ContentDialogResult.Primary ? view.SelectedPaths : files;
            await Task.Run(() => _store.Apply(batch, keep));
            _error = ""; _saved();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            _error = "录像仍保留在待确认区：" + e.Message;
            _retryAfter = DateTime.UtcNow.AddSeconds(5);
        }
        finally { view?.Release(); _dialog = null; _showing = false; }
    }
    public async Task StopAsync()
    {
        _disposed = true; _timer.Stop(); _store.Ready -= Ready; _dialog?.Hide();
        await _work;
    }
}
