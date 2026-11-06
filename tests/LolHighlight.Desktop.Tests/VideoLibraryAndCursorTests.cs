using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;
using LolHighlight.Desktop.ViewModels;
using Moq;

namespace LolHighlight.Desktop.Tests;

public sealed class VideoLibraryAndCursorTests
{
    [Theory]
    [InlineData("window")]
    [InlineData("display")]
    public void CaptureArguments_RecordCursorInBothModes(string mode)
    {
        using var pipeline = new FfmpegPipeline(new MemoryRingBuffer(10, 1024 * 1024), new AppConfig(), new RecordingClock());
        var args = pipeline.BuildArguments(new(42, new(0, 0, 320, 180), "x264", "p1", 30, "1M", false, false, true, "192k", 2, mode, 43), "libx264", null, null).ToArray();
        Assert.Contains(args, a => a.Contains("capture_cursor=1", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains("capture_cursor=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Library_IndexesEventsAndCombinesDatesSortAndFilter()
    {
        var a = new VideoItem("a.mp4", "a", "", new DateTime(2026, 11, 1));
        var b = new VideoItem("b.mp4", "b", "", new DateTime(2026, 11, 3));
        var c = new VideoItem("c.mp4", "c", "", new DateTime(2026, 11, 2));
        var scanner = new Mock<IVideoScanner>(); scanner.Setup(s => s.ScanVideos(It.IsAny<string>())).Returns([a, b, c]);
        var footer = new Mock<IMetadataFooterService>();
        foreach (var (video, kind) in new[] { (a, "kill"), (b, "multikill_5"), (c, "multikill_5") })
            footer.Setup(f => f.ReadMetadataFooter(video.Path)).Returns(new Dictionary<string, object> { ["triggers"] = new object[] { new Dictionary<string, object> { ["kind"] = kind } } });
        using var vm = new LibraryViewModel(scanner.Object, footer.Object, new AppPaths(), new AppConfig());
        await vm.RefreshAsync();
        Assert.Equal(new[] { b, c, a }, vm.Videos);
        vm.EventFilter = "multikill_5";
        vm.DateTo = new DateTimeOffset(new DateTime(2026, 11, 2));
        Assert.Same(c, Assert.Single(vm.Videos));
        Assert.Contains("五杀", Assert.Single(vm.Cards).TagText);
        vm.ResetFilters(); vm.SortOrder = "oldest";
        Assert.Equal(new[] { a, c, b }, vm.Videos);
    }

}
