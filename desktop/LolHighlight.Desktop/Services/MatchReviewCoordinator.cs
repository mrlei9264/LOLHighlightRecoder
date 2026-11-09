using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using LolHighlight.Desktop.Views;

namespace LolHighlight.Desktop.Services;

internal sealed class MatchReviewCoordinator
{
    private readonly Window _owner;
    private readonly MatchReviewStore _store;
    private readonly Action _saved;
    private readonly Action<int> _pendingChanged;
    private bool _showing, _disposed;
    private ContentDialog? _dialog;
    private Task _work = Task.CompletedTask;
    private int _countRequest;

    public MatchReviewCoordinator(Window owner, MatchReviewStore store, Action saved, Action<int> pendingChanged)
    {
        _owner = owner; _store = store; _saved = saved; _pendingChanged = pendingChanged;
        store.Ready += Ready;
        _ = RefreshPendingCountAsync();
    }
    private void Ready(object? sender, EventArgs e) => _owner.DispatcherQueue.TryEnqueue(() => _ = RefreshPendingCountAsync());

    public void ReviewPending() => StartDialog(ShowAsync);

    public void ReviewDiscarded() => StartDialog(ShowDiscardedAsync);

    private async Task RefreshPendingCountAsync()
    {
        if (_disposed) return;
        var request = ++_countRequest;
        try
        {
            var count = await Task.Run(() => _store.Pending().Count);
            if (!_disposed && request == _countRequest) _pendingChanged(count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private void StartDialog(Func<XamlRoot, Task> show)
    {
        if (_disposed || _showing || _owner.Content is not FrameworkElement { IsLoaded: true, XamlRoot: { } root }) return;
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0) return;
        _showing = true;
        _work = RunDialogAsync(root, show);
    }

    private async Task RunDialogAsync(XamlRoot root, Func<XamlRoot, Task> show)
    {
        try { await show(root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            if (!_disposed) await ShowMessageAsync(root, "录像处理失败", e.Message);
        }
        finally
        {
            _dialog = null; _showing = false;
            _ = RefreshPendingCountAsync();
        }
    }

    private async Task ShowAsync(XamlRoot root)
    {
        MatchReviewView? view = null;
        try
        {
            var batch = (await Task.Run(_store.Pending)).FirstOrDefault();
            if (_disposed) return;
            if (batch == null)
            {
                await ShowMessageAsync(root, "待确认录像", "当前没有待确认的录像。");
                return;
            }
            var files = _store.Files(batch);
            var width = Math.Max(320, Math.Min(1080, root.Size.Width - 48));
            view = new MatchReviewView { Width = Math.Max(280, width - 48), Height = Math.Max(220, Math.Min(570, root.Size.Height - 210)) };
            await view.LoadAsync(files);
            if (_disposed || VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0) return;
            _dialog = new ContentDialog { XamlRoot = root, Title = $"选择本局录像 · {batch.CreatedAt:MM-dd HH:mm}", Content = view,
                PrimaryButtonText = $"保存所选 ({view.KeepCount})", SecondaryButtonText = "全部保留",
                CloseButtonText = "稍后处理", DefaultButton = ContentDialogButton.None };
            _dialog.Resources["ContentDialogMaxWidth"] = width;
            _dialog.Resources["ContentDialogMaxHeight"] = Math.Max(300, root.Size.Height - 24);
            view.SelectionChanged += () => { if (_dialog != null) _dialog.PrimaryButtonText = view.KeepCount == 0 ? "全部丢弃" : $"保存所选 ({view.KeepCount})"; };
            var result = await _dialog.ShowAsync();
            await view.ReleaseAsync();
            if (_disposed || result == ContentDialogResult.None) return;
            var keep = result == ContentDialogResult.Primary ? view.SelectedPaths : files;
            await Task.Run(() => _store.Apply(batch, keep));
            if (!_disposed) _saved();
        }
        finally { view?.Release(); }
    }

    private async Task ShowDiscardedAsync(XamlRoot root)
    {
        var batches = await Task.Run(_store.Discarded);
        if (_disposed || VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0) return;
        if (batches.Count == 0)
        {
            await ShowMessageAsync(root, "已丢弃录像", "当前没有可恢复的录像。");
            return;
        }

        var options = new ComboBox { MinWidth = 260, Header = "选择对局" };
        foreach (var item in batches)
            options.Items.Add(new ComboBoxItem { Content = $"{item.Batch.CreatedAt:yyyy-MM-dd HH:mm} · {item.Count} 条", Tag = item.Batch });
        options.SelectedIndex = 0;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "丢弃的录像仍占用磁盘空间，可以恢复到视频库或手动清理。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(options);
        _dialog = new ContentDialog { XamlRoot = root, Title = "已丢弃录像", Content = content,
            PrimaryButtonText = "恢复到视频库", SecondaryButtonText = "永久清理", CloseButtonText = "关闭" };
        var result = await _dialog.ShowAsync();
        if (_disposed || options.SelectedItem is not ComboBoxItem { Tag: MatchReviewBatch batch }) return;
        if (result == ContentDialogResult.Primary)
        {
            await Task.Run(() => _store.RestoreDiscarded(batch));
            if (!_disposed) _saved();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            var files = await Task.Run(() => _store.DiscardedFiles(batch));
            if (_disposed || files.Count == 0) return;
            _dialog = new ContentDialog { XamlRoot = root, Title = "永久清理已丢弃录像",
                Content = $"将永久删除这场对局的 {files.Count} 条已丢弃录像。",
                PrimaryButtonText = "永久删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await _dialog.ShowAsync() == ContentDialogResult.Primary && !_disposed)
                await Task.Run(() => _store.PurgeDiscarded(batch, files));
        }
    }

    private async Task ShowMessageAsync(XamlRoot root, string title, string message)
    {
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0) return;
        _dialog = new ContentDialog { XamlRoot = root, Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "关闭" };
        await _dialog.ShowAsync();
    }

    public async Task StopAsync()
    {
        _disposed = true; _store.Ready -= Ready; _dialog?.Hide();
        await _work;
    }
}
