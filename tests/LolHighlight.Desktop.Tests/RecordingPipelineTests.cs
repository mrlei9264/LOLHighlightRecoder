using System.Diagnostics;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public sealed class RecordingPipelineTests
{
    [FfmpegFact]
    public async Task ExportAsync_SingleSegment_ProducesMp4()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pipeline_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var sourcePath = Path.Combine(root, "source.ts");
            await GenerateTestTransportStreamAsync(sourcePath);

            var ring = new MemoryRingBuffer(maxSeconds: 60, maxMemoryBytes: 32 * 1024 * 1024);
            ring.PushSegment(0, 0, 2, await File.ReadAllBytesAsync(sourcePath));

            var config = new AppConfig { FfmpegPath = FfmpegTestTools.FindExecutable()!, DiskQuotaGb = 100, MinimumFreeGb = 1, EmbedTelemetryFooter = true };
            var outputPath = Path.Combine(root, "highlight.mp4");
            var exporter = new ExportManager(
                ring, new MetadataFooterService(),
                new StorageQuotaManager(), config);

            var result = await exporter.ExportAsync(0, 2, outputPath);

            Assert.True(File.Exists(result));
            Assert.True(new FileInfo(result).Length > 0);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [FfmpegFact]
    public async Task ExportAsync_WithMetadata_WritesFooter()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pipeline_footer_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var sourcePath = Path.Combine(root, "source.ts");
            await GenerateTestTransportStreamAsync(sourcePath);

            var ring = new MemoryRingBuffer(maxSeconds: 60, maxMemoryBytes: 32 * 1024 * 1024);
            ring.PushSegment(0, 0, 2, await File.ReadAllBytesAsync(sourcePath));

            var config = new AppConfig { FfmpegPath = FfmpegTestTools.FindExecutable()!, DiskQuotaGb = 100, MinimumFreeGb = 1, EmbedTelemetryFooter = true };
            var outputPath = Path.Combine(root, "highlight.mp4");
            var exporter = new ExportManager(
                ring, new MetadataFooterService(),
                new StorageQuotaManager(), config);

            var metadata = new Dictionary<string, object>
            {
                ["type"] = "highlight",
                ["player"] = new Dictionary<string, string>
                {
                    ["game_name"] = "TestPlayer",
                    ["tag_line"] = "TAG"
                },
                ["triggers"] = new List<Dictionary<string, object>>
                {
                    new()
                    {
                        ["kind"] = "kill",
                        ["label"] = "击杀 Enemy"
                    }
                }
            };

            var result = await exporter.ExportAsync(0, 2, outputPath, metadata);

            var footer = new MetadataFooterService().ReadMetadataFooter(result);
            Assert.NotNull(footer);
            Assert.Equal("highlight", footer["type"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [FfmpegFact]
    public async Task ExportAsync_MultipleSegments_ConcatenatesCorrectly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pipeline_multi_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var source1Path = Path.Combine(root, "source1.ts");
            var source2Path = Path.Combine(root, "source2.ts");
            await GenerateTestTransportStreamAsync(source1Path);
            await GenerateTestTransportStreamAsync(source2Path);

            var ring = new MemoryRingBuffer(maxSeconds: 60, maxMemoryBytes: 32 * 1024 * 1024);
            ring.PushSegment(0, 0, 1, await File.ReadAllBytesAsync(source1Path));
            ring.PushSegment(1, 1, 2, await File.ReadAllBytesAsync(source2Path));

            var config = new AppConfig { FfmpegPath = FfmpegTestTools.FindExecutable()!, DiskQuotaGb = 100, MinimumFreeGb = 1 };
            var outputPath = Path.Combine(root, "highlight.mp4");
            var exporter = new ExportManager(
                ring, new MetadataFooterService(),
                new StorageQuotaManager(), config);

            var result = await exporter.ExportAsync(0, 2, outputPath);

            Assert.True(File.Exists(result));
            Assert.True(new FileInfo(result).Length > 0);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HighlightEvaluator_FullEventFlow()
    {
        var evaluator = new HighlightEvaluator();
        var player = new PlayerIdentity("riot", "TestPlayer", "TAG");

        var killEvent = new Dictionary<string, object>
        {
            ["EventName"] = "ChampionKill",
            ["EventID"] = 100,
            ["EventTime"] = 120.5,
            ["KillerName"] = "TestPlayer",
            ["VictimName"] = "EnemyPlayer"
        };

        var trigger = evaluator.Evaluate(killEvent, player);

        Assert.NotNull(trigger);
        Assert.Equal("kill", trigger!.Kind);
        Assert.Equal(100, trigger.EventId);
        Assert.Equal(120.5, trigger.EventTime);
    }

    [Fact]
    public async Task ClipCoordinator_TriggerAndExport_Flow()
    {
        var config = new AppConfig
        {
            PreRollSeconds = 5,
            PostRollSeconds = 3,
            SegmentGraceSeconds = 1,
            OutputDir = "test_output"
        };
        var clock = new FakeClock();
        var paths = new AppPaths();
        var exporter = new FakeExportManager();
        var coordinator = new ClipCoordinator(config, exporter, clock, paths);

        var player = new PlayerIdentity("riot", "TestPlayer", "TAG");
        var trigger = new HighlightTrigger(
            1, "kill", "击杀 Enemy", 10.0,
            DateTime.UtcNow, new Dictionary<string, object>());

        coordinator.Trigger(trigger, player);

        Assert.NotNull(coordinator.ActiveWindow);
        Assert.Single(coordinator.ActiveWindow.Triggers);

        clock.NowSeconds = 20;
        var result = await coordinator.TickAsync(20, force: true);

        Assert.NotNull(result);
        Assert.Null(coordinator.ActiveWindow);
        Assert.NotNull(exporter.LastMetadata);
    }

    [Fact]
    public void MemoryRingBuffer_PushAndGetRange_FullCycle()
    {
        var ring = new MemoryRingBuffer(maxSeconds: 60, maxMemoryBytes: 1024 * 1024);

        var data1 = new byte[] { 1, 2, 3, 4 };
        var data2 = new byte[] { 5, 6, 7, 8 };
        var data3 = new byte[] { 9, 10, 11, 12 };

        ring.PushSegment(0, 0, 1, data1);
        ring.PushSegment(1, 1, 2, data2);
        ring.PushSegment(2, 2, 3, data3);

        Assert.Equal(3, ring.SegmentCount);

        var range = ring.GetRange(0.5, 2.5).ToList();
        Assert.Equal(3, range.Count);
        Assert.Equal(0u, range[0].Index);
        Assert.Equal(1u, range[1].Index);
        Assert.Equal(2u, range[2].Index);

        Assert.Equal(12, ring.MemoryUsage);
    }

    [Fact]
    public void StorageQuotaManager_Enforce_DeletesOldestFilesWhenOverQuota()
    {
        var manager = new StorageQuotaManager();

        var root = Path.Combine(Path.GetTempPath(), $"quota_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var file1 = Path.Combine(root, "highlight_old1.mp4");
            var file2 = Path.Combine(root, "highlight_old2.mp4");
            File.WriteAllBytes(file1, new byte[1024 * 1024]);
            File.WriteAllBytes(file2, new byte[1024 * 1024]);

            File.SetLastWriteTimeUtc(file1, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(file2, DateTime.UtcNow.AddDays(-5));

            manager.Enforce(root, 0, 0);

            Assert.False(File.Exists(file1));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<bool> GenerateTestTransportStreamAsync(string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegTestTools.FindExecutable() ?? throw new InvalidOperationException("FFmpeg unavailable"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10",
            "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-f", "mpegts", outputPath
        })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;

        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return true;
    }

    private sealed class FakeClock : IRecordingClock
    {
        public bool IsRunning => true;
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public double NowSeconds { get; set; }
        public void Start() { }
        public void Stop() { }
        public double ToSessionSeconds(DateTime utcTime) => NowSeconds;
    }

    private sealed class FakeExportManager : IExportManager
    {
        public Dictionary<string, object>? LastMetadata { get; private set; }

        public Task<string> ExportAsync(
            double startTime, double endTime, string outputPath,
            Dictionary<string, object>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            LastMetadata = metadata;
            File.WriteAllText(outputPath, "fake_video");
            return Task.FromResult(outputPath);
        }
    }
}
