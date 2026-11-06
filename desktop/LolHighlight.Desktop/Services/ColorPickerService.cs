using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pickers = Microsoft.Windows.Storage.Pickers;

namespace LolHighlight.Desktop.Services;

public sealed record ExportDestination(string Folder, bool Remember);

public interface IColorPickerService
{
    Task<IReadOnlyList<string>> PickVideosAsync();
    Task<string?> PickFolderAsync();
    Task<ExportDestination?> PickExportDestinationAsync(string defaultFolder);
}

public sealed class ColorPickerService(Func<Window?> owner) : IColorPickerService
{
    private WindowId OwnerId => Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(owner() ?? throw new InvalidOperationException("主窗口尚未就绪。")));
    public async Task<IReadOnlyList<string>> PickVideosAsync()
    {
        var picker = new Pickers.FileOpenPicker(OwnerId) { CommitButtonText = "选择待调色视频" };
        foreach (var ext in new[] { ".mp4", ".mkv", ".mov", ".ts" }) picker.FileTypeFilter.Add(ext);
        return (await picker.PickMultipleFilesAsync())?.Select(f => f.Path).ToArray() ?? Array.Empty<string>();
    }
    public async Task<string?> PickFolderAsync() => (await new Pickers.FolderPicker(OwnerId) { CommitButtonText = "选择输出目录" }.PickSingleFolderAsync())?.Path;

    public async Task<ExportDestination?> PickExportDestinationAsync(string defaultFolder)
    {
        var folder = Path.GetFullPath(defaultFolder);
        var path = new TextBlock { Text = folder, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var browse = new Button { Content = "选择导出文件夹…" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        browse.Click += async (_, _) =>
        {
            browse.IsEnabled = false;
            try { var chosen = await PickFolderAsync(); if (chosen != null) path.Text = folder = chosen; error.Text = ""; }
            catch (Exception e) { error.Text = e.Message; }
            finally { browse.IsEnabled = true; }
        };
        var remember = new CheckBox { Content = "不再询问，使用此位置作为默认位置" };
        var panel = new StackPanel { Spacing = 16, MinWidth = 280 };
        panel.Children.Add(path); panel.Children.Add(browse); panel.Children.Add(remember); panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = (owner()?.Content as FrameworkElement)?.XamlRoot,
            Title = "导出调色视频", Content = panel, PrimaryButtonText = "开始生成", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? new(folder, remember.IsChecked == true) : null;
    }
}
