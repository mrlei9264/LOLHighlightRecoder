using System.Diagnostics;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Infrastructure;

namespace LolHighlight.Recording.Services;

public interface IExportManager
{
    // The complete GOPs actually committed can extend beyond the requested end.
    double? LastExportedEnd => null;
    string? LastExportWarning => null;
    Task<string> ExportAsync(
        double startTime,
        double endTime,
        string outputPath,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);
}

public sealed class ExportManager : IExportManager
{
    public double? LastExportedEnd { get; private set; }
    public string? LastExportWarning { get; private set; }
    private readonly IMemoryRingBuffer _ringBuffer;
    private readonly IMetadataFooterService _metadataFooter;
    private readonly IStorageQuotaManager _quotaManager;
    private readonly AppConfig _config;

    public ExportManager(
        IMemoryRingBuffer ringBuffer,
        IMetadataFooterService metadataFooter,
        IStorageQuotaManager quotaManager,
        AppConfig config)
    {
        _ringBuffer = ringBuffer;
        _metadataFooter = metadataFooter;
        _quotaManager = quotaManager;
        _config = config;
    }

    public async Task<string> ExportAsync(
        double startTime,
        double endTime,
        string outputPath,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        LastExportWarning = null;
        if (!double.IsFinite(startTime) || !double.IsFinite(endTime) || endTime <= startTime)
            throw new ArgumentException("Export end time must be after start time");

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))
            ?? throw new ArgumentException("Output path has no directory", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(outputPath))
            throw new IOException("Output video already exists");
        var segments = _ringBuffer.GetRange(startTime, endTime)
            .OrderBy(segment => segment.Index)
            .ToList();
        if (segments.Count == 0)
            throw new InvalidOperationException("No segments found in time range");

        for (var i = 1; i < segments.Count; i++)
        {
            if (segments[i].Index != segments[i - 1].Index + 1 ||
                segments[i].TimestampStart - segments[i - 1].TimestampEnd > 0.25)
                throw new InvalidOperationException("The requested range is no longer fully buffered");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var expectedBytes = checked((long)(segments.Sum(segment => (long)segment.Data.Length) * 1.05) + 1024 * 1024);
        _quotaManager.EnsureRoom(outputDirectory, _config.DiskQuotaGb, _config.MinimumFreeGb, outputPath, expectedBytes);

        string? chapterMetadata = null;
        if (metadata != null)
        {
            metadata = new Dictionary<string, object>(metadata)
            {
                ["requested_window"] = new Dictionary<string, double> { ["start_seconds"] = startTime, ["end_seconds"] = endTime },
                ["clip_window"] = new Dictionary<string, double>
                {
                    ["start_seconds"] = segments[0].TimestampStart,
                    ["end_seconds"] = segments[^1].TimestampEnd
                },
                ["partial_buffer"] = segments[0].TimestampStart > startTime + 0.25
            };
            if (metadata.Remove(VideoChapters.ExportMetadataKey, out var events) && events is RecordingChapter[] chapters)
                chapterMetadata = VideoChapters.CreateMetadata(chapters, segments[0].TimestampStart, segments[^1].TimestampEnd);
        }

        var partialPath = outputPath + ".part.mp4";
        var chapterPath = chapterMetadata == null ? null : $"{outputPath}.{Guid.NewGuid():N}.ffmeta";
        var preservePartial = false;

        try
        {
            if (chapterPath != null)
                await File.WriteAllTextAsync(chapterPath, chapterMetadata!, cancellationToken);
            await RemuxToMp4Async(segments, partialPath, chapterPath, cancellationToken);
            var video = new FileInfo(partialPath);
            if (!video.Exists || video.Length == 0)
                throw new IOException("FFmpeg did not produce a video file");

            if (metadata != null && _config.EmbedTelemetryFooter)
            {
                var videoLength = video.Length;
                try
                {
                    if (metadata.Remove("_telemetry_archive", out var archive) && archive is TelemetryArchiveRequest request)
                    {
                        var actualWindow = request with
                        {
                            Start = request.Start.AddSeconds(segments[0].TimestampStart - startTime),
                            End = request.End.AddSeconds(segments[^1].TimestampEnd - endTime)
                        };
                        var result = await TelemetryArchive.AppendAsync(partialPath, actualWindow, cancellationToken,
                            incoming => (_quotaManager as StorageQuotaManager ?? new StorageQuotaManager()).EnsurePendingAppendRoom(partialPath, outputPath,
                                _config.DiskQuotaGb, _config.MinimumFreeGb, incoming));
                        metadata["telemetry_archive"] = new { format = "gzip-ndjson-uuid-v1", result.Chunks, result.Records, result.Bytes };
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    _metadataFooter.AppendMetadataFooter(partialPath, metadata);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Both metadata writers only append. Remove any incomplete boxes before publishing the video.
                    try
                    {
                        using var stream = File.Open(partialPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                        if (stream.Length < videoLength) throw new IOException("视频文件意外缩短");
                        if (stream.Length > videoLength) stream.SetLength(videoLength);
                    }
                    catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                    {
                        preservePartial = true;
                        throw new IOException($"无法恢复录像到写入对局数据前的状态，已保留文件：{partialPath}", rollbackError);
                    }
                    LastExportWarning = $"高光录像已保存，对局数据写入失败：{error.Message}";
                }
            }

            File.Move(partialPath, outputPath, overwrite: false);

            LastExportedEnd = segments[^1].TimestampEnd;

            _quotaManager.Enforce(outputDirectory, _config.DiskQuotaGb, _config.MinimumFreeGb, outputPath);
            return outputPath;
        }
        finally
        {
            if (!preservePartial)
                try { File.Delete(partialPath); } catch { }
            if (chapterPath != null) VideoChapters.DeleteTemporary(chapterPath);
        }
    }

    private async Task RemuxToMp4Async(
        IReadOnlyList<MemorySegment> segments,
        string outputPath,
        string? chapterPath,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        cancellationToken = timeout.Token;
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegExecutable.Resolve(_config.FfmpegPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "warning", "-y",
            "-fflags", "+genpts", "-f", "mpegts", "-i", "pipe:0"
        })
            startInfo.ArgumentList.Add(argument);
        if (chapterPath != null)
        {
            foreach (var argument in new[] { "-f", "ffmetadata", "-i", chapterPath, "-map_chapters", "1" })
                startInfo.ArgumentList.Add(argument);
        }
        foreach (var argument in new[] { "-map", "0", "-c", "copy", "-movflags", "+faststart", outputPath })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start FFmpeg export process");
        var errorTask = ProcessRunner.ReadBoundedTextAsync(process.StandardError);
        try
        {
            await using var input = process.StandardInput.BaseStream;
            foreach (var segment in segments)
                await input.WriteAsync(segment.Data.AsMemory(), cancellationToken);
            await input.FlushAsync(cancellationToken);

            try { process.StandardInput.Close(); } catch { }
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch { }
            await errorTask;
            throw;
        }
        var error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg export failed with exit code {process.ExitCode}: {error.Trim()}");
    }
}
