using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;

namespace LolHighlight.Recording.Services;

internal readonly record struct RecordingChapter(double SessionSeconds, string Kind);
internal sealed record PlaybackChapter(double Seconds, string Title);

internal static class VideoChapters
{
    internal const string ExportMetadataKey = "_chapters";

    internal static async Task<IReadOnlyList<PlaybackChapter>> ReadAsync(string path, AppConfig config, CancellationToken token)
    {
        IReadOnlyList<PlaybackChapter> embedded = [];
        try { embedded = await ReadEmbeddedAsync(path, config, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or
            System.ComponentModel.Win32Exception or JsonException or InvalidOperationException) { }

        var stored = await Task.Run(() => ReadStoredHighlightEvents(path), token);
        if (stored.Count == 0) return embedded;
        var markers = embedded.ToList();
        // Some older MP4 files moved the first event chapter to zero. The original clip times are more precise.
        if (markers.Count > 0 && markers[0].Seconds == 0 && stored[0].Seconds > 0.002 && HasTitle(markers[0], stored[0].Title))
            markers.RemoveAt(0);
        foreach (var chapter in stored)
        {
            if (!markers.Any(marker => Math.Abs(marker.Seconds - chapter.Seconds) <= 0.002 && HasTitle(marker, chapter.Title)))
                markers.Add(chapter);
        }
        return markers.OrderBy(chapter => chapter.Seconds).ToArray();
    }

    private static bool HasTitle(PlaybackChapter chapter, string title) =>
        chapter.Title == title || chapter.Title.Split(" / ", StringSplitOptions.None).Contains(title, StringComparer.Ordinal);

    private static async Task<IReadOnlyList<PlaybackChapter>> ReadEmbeddedAsync(string path, AppConfig config, CancellationToken token)
    {
        var ffmpeg = FfmpegExecutable.Resolve(config.FfmpegPath);
        var startInfo = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe"))
        {
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "chapter=start_time:chapter_tags=title",
            "-of", "json=compact=1", Path.GetFullPath(path) })
            startInfo.ArgumentList.Add(argument);
        var result = await ProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(8), token, maxOutputChars: 2 * 1024 * 1024);
        if (result.ExitCode != 0) return [];

        using var document = JsonDocument.Parse(result.Output);
        if (!document.RootElement.TryGetProperty("chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
            return [];
        var markers = new List<PlaybackChapter>();
        foreach (var chapter in chapters.EnumerateArray())
        {
            if (!chapter.TryGetProperty("start_time", out var start) ||
                !double.TryParse(start.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
                continue;
            var title = chapter.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            markers.Add(new(seconds, string.IsNullOrWhiteSpace(title) ? $"事件 {markers.Count + 1}" : title));
        }
        return markers.Distinct().OrderBy(chapter => chapter.Seconds).ToArray();
    }

    private static IReadOnlyList<PlaybackChapter> ReadStoredHighlightEvents(string path)
    {
        try
        {
            var metadata = new MetadataFooterService().ReadMetadataFooter(path);
            if (metadata?.GetValueOrDefault("type") is not "highlight" ||
                metadata.GetValueOrDefault("clip_window") is not Dictionary<string, object> window ||
                metadata.GetValueOrDefault("triggers") is not IEnumerable<object> triggers ||
                !TryGetSeconds(window, "start_seconds", out var start) ||
                !TryGetSeconds(window, "end_seconds", out var end))
                return [];
            var chapters = new List<PlaybackChapter>();
            foreach (var trigger in triggers.OfType<Dictionary<string, object>>())
            {
                if (!TryGetSeconds(trigger, "session_seconds", out var seconds) || seconds < start || seconds >= end)
                    continue;
                var label = trigger.GetValueOrDefault("label") as string;
                chapters.Add(new(seconds - start, string.IsNullOrWhiteSpace(label) ? "事件" : label));
            }
            return chapters.Distinct().OrderBy(chapter => chapter.Seconds).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or OverflowException) { return []; }
    }

    private static bool TryGetSeconds(Dictionary<string, object> data, string key, out double seconds)
        => double.TryParse(Convert.ToString(data.GetValueOrDefault(key), CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) &&
            double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds;

    internal static string? CreateMetadata(IEnumerable<RecordingChapter> events, double videoStart, double videoEnd)
    {
        if (!double.IsFinite(videoStart) || !double.IsFinite(videoEnd) || videoEnd <= videoStart) return null;

        var end = (long)Math.Ceiling((videoEnd - videoStart) * 1000);
        var markers = events.Where(item => item.SessionSeconds >= videoStart && item.SessionSeconds < videoEnd)
            .GroupBy(item => (long)Math.Floor((item.SessionSeconds - videoStart) * 1000))
            .Where(group => group.Key < end)
            .OrderBy(group => group.Key)
            .ToArray();
        if (markers.Length == 0) return null;

        var metadata = new StringBuilder(";FFMETADATA1\n");
        // The MP4 chapter track begins at zero. Reserve that position when the first event occurs later.
        if (markers[0].Key > 0)
            AppendChapter(metadata, 0, markers[0].Key, "录像开始");
        for (var i = 0; i < markers.Length; i++)
        {
            var title = string.Join(" / ", markers[i].Select(item => Label(item.Kind)).Distinct());
            var chapterEnd = i + 1 < markers.Length ? markers[i + 1].Key : end;
            AppendChapter(metadata, markers[i].Key, chapterEnd, title);
        }
        return metadata.ToString();
    }

    private static void AppendChapter(StringBuilder metadata, long start, long end, string title)
    {
        metadata.Append(CultureInfo.InvariantCulture, $"[CHAPTER]\nTIMEBASE=1/1000\nSTART={start}\nEND={end}\n");
        metadata.Append("title=").Append(Escape(title)).Append('\n');
    }

    private static string Label(string kind) => RecordingEventCatalog.All.FirstOrDefault(item => item.Id == kind)?.Label ?? kind;

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("=", "\\=")
        .Replace(";", "\\;").Replace("#", "\\#").Replace('\r', ' ').Replace('\n', ' ');

    internal static async Task AddToFullRecordingAsync(string video, string metadata, AppConfig config)
    {
        var info = new FileInfo(video);
        var drive = new DriveInfo(Path.GetPathRoot(info.FullName)!);
        if (drive.AvailableFreeSpace < info.Length + 16 * 1024 * 1024L + config.MinimumFreeGb * 1024L * 1024 * 1024)
            throw new IOException("磁盘剩余空间不足，无法临时封装章节。");

        // Keep the original until the stream copy finishes. The temporary MP4 is excluded from library scans and quota cleanup.
        var temporary = $"{video}.{Guid.NewGuid():N}";
        var output = temporary + ".part.mp4";
        var chapters = temporary + ".ffmeta";
        try
        {
            await File.WriteAllTextAsync(chapters, metadata);
            var startInfo = new ProcessStartInfo(FfmpegExecutable.Resolve(config.FfmpegPath));
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "warning", "-nostdin", "-n", "-i", video,
                "-f", "ffmetadata", "-i", chapters, "-map", "0", "-map_metadata", "0", "-map_chapters", "1", "-c", "copy", output })
                startInfo.ArgumentList.Add(argument);
            var result = await ProcessRunner.RunAsync(startInfo, TimeSpan.FromMinutes(30), CancellationToken.None);
            if (result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
                throw new IOException($"FFmpeg 无法写入章节：{result.Error.Trim()}");
            File.Move(output, video, overwrite: true);
        }
        finally
        {
            DeleteTemporary(chapters);
            DeleteTemporary(output);
        }
    }

    internal static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
