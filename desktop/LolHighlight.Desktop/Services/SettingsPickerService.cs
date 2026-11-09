using Microsoft.UI;
using Microsoft.UI.Xaml;
using Pickers = Microsoft.Windows.Storage.Pickers;

namespace LolHighlight.Desktop.Services;

public interface ISettingsPickerService
{
    Task<string?> PickOutputFolderAsync();
    Task<string?> PickFfmpegExecutableAsync();
    Task<string?> PickLeagueClientAsync();
}

public sealed class SettingsPickerService(Func<Window?> owner) : ISettingsPickerService
{
    private WindowId OwnerId
    {
        get
        {
            var window = owner() ?? throw new InvalidOperationException("主窗口尚未就绪，请稍后重试。");
            return Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        }
    }

    public async Task<string?> PickOutputFolderAsync()
    {
        var picker = new Pickers.FolderPicker(OwnerId)
        {
            SuggestedStartLocation = Pickers.PickerLocationId.ComputerFolder,
            CommitButtonText = "选择录像目录"
        };
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public Task<string?> PickFfmpegExecutableAsync() => PickExecutableAsync("使用此程序");

    public Task<string?> PickLeagueClientAsync() => PickExecutableAsync("选择 TCLS\\Client.exe");

    private async Task<string?> PickExecutableAsync(string commitButtonText)
    {
        var picker = new Pickers.FileOpenPicker(OwnerId)
        {
            SuggestedStartLocation = Pickers.PickerLocationId.ComputerFolder,
            CommitButtonText = commitButtonText
        };
        picker.FileTypeFilter.Add(".exe");
        return (await picker.PickSingleFileAsync())?.Path;
    }
}
