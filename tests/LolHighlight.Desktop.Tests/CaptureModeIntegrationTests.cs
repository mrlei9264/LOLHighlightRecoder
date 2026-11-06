using System.Diagnostics;
using System.Runtime.InteropServices;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

[Collection("ScreenCapture")]
public sealed class CaptureModeIntegrationTests
{
    [FfmpegFact]
    [Trait("Category", "WindowCapture")]
    public async Task DisplayCapture_ExcludesNotificationWindowAndShowsUnderlyingPixels()
    {
        using var background = new NativeCaptureWindow(0x90000006, 24, 24, 320, 180); // white rectangle
        var backgroundHandle = await background.Handle;
        SetWindowPos((nint)backgroundHandle, new nint(-1), 24, 24, 320, 180, 0x0010 | 0x0040);
        using var overlay = new NativeCaptureWindow(0x90000004, 48, 48, 100, 100); // black rectangle
        var overlayHandle = await overlay.Handle;
        SetWindowPos((nint)overlayHandle, new nint(-1), 48, 48, 100, 100, 0x0010 | 0x0040);
        DwmFlush();
        var monitor = MonitorFromWindow((nint)backgroundHandle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Assert.True(GetMonitorInfo(monitor, ref info));
        using var directory = new TestDirectory();
        async Task<byte[]> Sample(string name)
        {
            var output = Path.Combine(directory.Path, name);
            // Only pixels inside the test windows are downloaded and written to disk.
            await FfmpegTestTools.RunAsync("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                $"gfxcapture=hmonitor={monitor.ToInt64()}:capture_cursor=0:output_fmt=bgra:crop_left={70 - info.Monitor.Left}:crop_top={70 - info.Monitor.Top}:crop_right={info.Monitor.Right - 86}:crop_bottom={info.Monitor.Bottom - 86}",
                "-vf", "hwdownload,format=bgra,format=rgb24", "-ss", "0.2", "-frames:v", "1", "-f", "rawvideo", output);
            return await File.ReadAllBytesAsync(output);
        }
        var before = await Sample("before.rgb");
        // SS_BLACKRECT uses the theme's window-frame color, which can be dark gray on Windows 11.
        Assert.True(before.Average(value => (double)value) < 180, $"The test overlay must be visible before exclusion (average {before.Average(value => (double)value)}).");
        Assert.True(CaptureExclusion.TryEnable((nint)overlayHandle));
        Assert.True(GetWindowDisplayAffinity((nint)overlayHandle, out var affinity));
        Assert.Equal(0x11u, affinity);
        var after = await Sample("after.rgb");
        Assert.True(after.Average(value => (double)value) > 240, $"Excluded overlay must reveal the underlying window, rather than a black rectangle (average {after.Average(value => (double)value)}).");
    }

    [FfmpegFact]
    [Trait("Category", "WindowCapture")]
    public async Task LivePipeline_RecordsOneWholeMatchFileWithoutAnyHighlightTrigger()
    {
        using var window = new NativeCaptureWindow();
        var hwnd = await window.Handle;
        using var directory = new TestDirectory();
        var video = Path.Combine(directory.Path, "fullgame_capture.mp4");
        var config = new AppConfig { FfmpegPath = FfmpegTestTools.FindExecutable()!, MinimumFreeGb = 1 };
        var ring = new MemoryRingBuffer(2, 64 * 1024 * 1024);
        var clock = new RecordingClock();
        clock.Start();
        using var pipeline = new FfmpegPipeline(ring, config, clock);
        await pipeline.StartAsync(new(hwnd, new(0, 0, 320, 180), "x264", "p1", 10, "1M", false, false, true,
            "192k", 2, FullGameOutputPath: video));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (clock.NowSeconds < 6) await timer.WaitForNextTickAsync(timeout.Token);
        await pipeline.StopAsync();
        Assert.True(File.Exists(video));
        Assert.Single(Directory.GetFiles(directory.Path, "*.mp4"));
        var frames = Path.Combine(directory.Path, "fullgame.md5");
        await FfmpegTestTools.RunAsync("-hide_banner", "-loglevel", "error", "-xerror", "-i", video, "-f", "framemd5", frames);
        Assert.True(File.ReadLines(frames).Count(line => line.Length > 0 && !line.StartsWith('#')) >= 40);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}

[CollectionDefinition("ScreenCapture", DisableParallelization = true)]
public sealed class ScreenCaptureCollection { }
