using System.Diagnostics;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public sealed class ExportManagerTests
{
    [FfmpegFact]
    public async Task ExportAsync_StreamsMemorySegmentsToFfmpegAndWritesFooter()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rift_replay_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var sourcePath = Path.Combine(root, "source.ts");
            await GenerateTestTransportStreamAsync(sourcePath);

            var ring = new MemoryRingBuffer(maxSeconds: 60, maxMemoryBytes: 32 * 1024 * 1024);
            ring.PushSegment(0, 0, 2, await File.ReadAllBytesAsync(sourcePath));

            var config = new AppConfig
            {
                FfmpegPath = FfmpegTestTools.FindExecutable()!,
                DiskQuotaGb = 100,
                MinimumFreeGb = 1,
                EmbedTelemetryFooter = true
            };
            var outputPath = Path.Combine(root, "highlight.mp4");
            var exporter = new ExportManager(
                ring,
                new MetadataFooterService(),
                new StorageQuotaManager(),
                config);

            await exporter.ExportAsync(
                0,
                2,
                outputPath,
                new Dictionary<string, object> { ["test"] = "stdin-export" });

            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);
            var footer = new MetadataFooterService().ReadMetadataFooter(outputPath);
            Assert.Equal("stdin-export", footer!["test"]?.ToString());
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
            "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-f", "mpegts", outputPath
        })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;

        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return true;
    }
}
