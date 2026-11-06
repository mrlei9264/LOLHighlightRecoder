using LolHighlight.Desktop.ViewModels;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Services;
using Moq;

namespace LolHighlight.Desktop.Tests;

public sealed class LibraryViewModelTests
{
    [Fact]
    public async Task ChangingSelectionWhileMetadataLoads_DoesNotDisplayPreviousPlayersData()
    {
        var first = new VideoItem("first.mp4", "first", "");
        var second = new VideoItem("second.mp4", "second", "");
        var scanner = new Mock<IVideoScanner>();
        scanner.Setup(x => x.ScanVideos(It.IsAny<string>())).Returns(new List<VideoItem> { first, second });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var footer = new Mock<IMetadataFooterService>();
        footer.Setup(x => x.ReadMetadataFooter(first.Path)).Returns(() =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Metadata("PreviousPlayer");
        });
        footer.Setup(x => x.ReadMetadataFooter(second.Path)).Returns(Metadata("CurrentPlayer"));
        using var vm = new LibraryViewModel(scanner.Object, footer.Object, new AppPaths(), new AppConfig());
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.SelectedPlayer) && vm.SelectedPlayer == "CurrentPlayer")
                loaded.TrySetResult();
        };
        var refresh = vm.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.SelectedVideo = second;
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        await refresh;
        Assert.Equal("CurrentPlayer", vm.SelectedPlayer);
        Assert.Same(second, vm.SelectedVideo);
    }

    [Fact]
    public async Task ExportTelemetry_WritesEveryArchivedRecordToASeparateFile_WithoutChangingVideo()
    {
        using var directory = new TestDirectory();
        var video = Path.Combine(directory.Path, "fullgame_test.mp4");
        var spool = Path.Combine(directory.Path, "telemetry.ndjson");
        var now = DateTime.UtcNow;
        await File.WriteAllBytesAsync(video, new byte[] { 0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p' });
        await File.WriteAllTextAsync(spool, System.Text.Json.JsonSerializer.Serialize(new { type = "snapshot", captured_at = now, test_data = 123 }) + "\n");
        await TelemetryArchive.AppendAsync(video, new(spool, now.AddSeconds(-1), now.AddSeconds(1)));
        var before = await File.ReadAllBytesAsync(video);
        using var vm = new LibraryViewModel(Mock.Of<IVideoScanner>(), new MetadataFooterService(), new AppPaths(), new());
        vm.SelectedVideo = new(video, "fullgame_test", "");
        await ((AsyncRelayCommand)vm.ExportTelemetryCommand).ExecuteAsync(null);
        var exported = Assert.Single(Directory.GetFiles(directory.Path, "*.data_*.ndjson"));
        Assert.Equal(await File.ReadAllTextAsync(spool), await File.ReadAllTextAsync(exported));
        Assert.Equal(before, await File.ReadAllBytesAsync(video));
        Assert.False(vm.IsStatusError, vm.StatusMessage);
    }

    private static Dictionary<string, object> Metadata(string player) => new()
    {
        ["player"] = new Dictionary<string, object> { ["game_name"] = player }
    };
}
