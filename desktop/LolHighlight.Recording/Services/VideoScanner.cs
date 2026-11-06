using LolHighlight.Recording.Models;

namespace LolHighlight.Recording.Services;

public interface IVideoScanner
{
    List<VideoItem> ScanVideos(string directory);
    List<VideoItem> ScanRecentVideos(string directory, int count) => ScanVideos(directory).Take(count).ToList();
}

public sealed class VideoScanner : IVideoScanner
{
    public List<VideoItem> ScanVideos(string directory) => EnumerateVideos(directory)
        .OrderByDescending(video => video.CreatedAt)
        .ThenByDescending(video => video.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public List<VideoItem> ScanRecentVideos(string directory, int count)
    {
        if (count <= 0) return new();
        var latest = new PriorityQueue<VideoItem, DateTime>();
        foreach (var video in EnumerateVideos(directory))
        {
            latest.Enqueue(video, video.CreatedAt);
            if (latest.Count > count) latest.Dequeue();
        }
        return latest.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(video => video.CreatedAt).ToList();
    }

    private static IEnumerable<VideoItem> EnumerateVideos(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        using var files = Directory.EnumerateFiles(directory, "*.mp4", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }).GetEnumerator();
        while (true)
        {
            string file;
            try
            {
                if (!files.MoveNext()) yield break;
                file = files.Current;
                if (Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".pending", StringComparer.OrdinalIgnoreCase)) continue;
            }
            catch (IOException) { yield break; }
            catch (UnauthorizedAccessException) { yield break; }
            VideoItem? video = null;
            try
            {
                if (!file.EndsWith(".part.mp4", StringComparison.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(file);
                    if (info.Exists && info.Length > 0)
                        video = new VideoItem(file, Path.GetFileNameWithoutExtension(file),
                            $"{info.LastWriteTime:yyyy-MM-dd HH:mm}", info.LastWriteTime, info.Length);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (video != null) yield return video;
        }
    }
}
