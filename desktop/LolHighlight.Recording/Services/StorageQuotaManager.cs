using System.Text.Json;
using LolHighlight.Recording.Infrastructure;

namespace LolHighlight.Recording.Services;

public interface IStorageQuotaManager
{
    void EnsureRoom(string outputDirectory, int quotaGb, int minimumFreeGb, string? protectedPath = null, long incomingBytes = 0);
    void Enforce(string outputDirectory, int quotaGb, int minimumFreeGb, string? protectedPath = null, long incomingBytes = 0);
}

public sealed class StorageQuotaManager(AppPaths? paths = null) : IStorageQuotaManager
{
    private static readonly object RenameGate = new();
    private readonly string _renameIndex = Path.Combine((paths ?? new AppPaths()).DataRoot, "recording-renames.json");

    public void MoveVideo(string sourcePath, string destinationPath)
    {
        lock (RenameGate)
        {
            if (!HasRecordingPrefix(Path.GetFileName(destinationPath)))
            {
                var index = ReadRenameIndex();
                var source = new FileInfo(sourcePath);
                if (IsManaged(source, index))
                {
                    foreach (var path in index.Keys.Where(path => !File.Exists(path)).ToArray()) index.Remove(path);
                    // Stage the new path before moving, so a failed or interrupted move preserves the old entry.
                    index[destinationPath] = VideoStamp.From(source);
                    Directory.CreateDirectory(Path.GetDirectoryName(_renameIndex)!);
                    var temp = _renameIndex + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(index));
                    File.Move(temp, _renameIndex, overwrite: true);
                }
            }
            File.Move(sourcePath, destinationPath);
        }
    }

    public void EnsurePendingAppendRoom(string pendingPath, string outputPath, int quotaGb, int minimumFreeGb, long incomingBytes)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var total = checked(new FileInfo(pendingPath).Length + incomingBytes);
        if (total > quotaGb * 1024L * 1024 * 1024) throw new IOException("当前录像和对局数据超过录像空间上限");
        Enforce(directory, quotaGb, minimumFreeGb, outputPath, total);
        if (new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < minimumFreeGb * 1024L * 1024 * 1024 + incomingBytes)
            throw new IOException("剩余磁盘空间不足，已保留现有视频和原始对局数据");
    }

    public void EnsureRoom(string outputDirectory, int quotaGb, int minimumFreeGb, string? protectedPath = null, long incomingBytes = 0)
    {
        outputDirectory = RecordingRoot(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (incomingBytes > quotaGb * 1024L * 1024 * 1024)
            throw new IOException("单个高光超过录像配额，请提高配额或缩短片段时长");
        Enforce(outputDirectory, quotaGb, minimumFreeGb, protectedPath, incomingBytes);
        var total = ManagedVideos(outputDirectory).Sum(file => file.Length);
        if (total + incomingBytes > quotaGb * 1024L * 1024 * 1024)
            throw new IOException("录像空间已达上限，请先处理待确认录像或提高容量上限。");

        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outputDirectory))!);
        if (drive.AvailableFreeSpace < minimumFreeGb * 1024L * 1024L * 1024L + incomingBytes)
            throw new IOException($"Not enough free disk space on {drive.Name}");
    }

    public void Enforce(string outputDirectory, int quotaGb, int minimumFreeGb, string? protectedPath = null, long incomingBytes = 0)
    {
        // A file must stay at the path used to count it until this cleanup finishes.
        lock (RenameGate) EnforceCore(outputDirectory, quotaGb, minimumFreeGb, protectedPath, incomingBytes);
    }

    private void EnforceCore(string outputDirectory, int quotaGb, int minimumFreeGb, string? protectedPath, long incomingBytes)
    {
        outputDirectory = RecordingRoot(outputDirectory);
        if (!Directory.Exists(outputDirectory))
            return;

        var protectedFullPath = protectedPath == null
            ? null
            : Path.GetFullPath(protectedPath);

        var files = ManagedVideos(outputDirectory).OrderBy(file => file.LastWriteTimeUtc).ToList();

        var quotaBytes = quotaGb * 1024L * 1024L * 1024L;
        var totalBytes = files.Sum(file => file.Length) + incomingBytes;
        var root = Path.GetPathRoot(Path.GetFullPath(outputDirectory));
        var drive = root == null ? null : new DriveInfo(root);
        var minimumFreeBytes = minimumFreeGb * 1024L * 1024L * 1024L + incomingBytes;

        foreach (var file in files)
        {
            if (file.FullName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".pending", StringComparer.OrdinalIgnoreCase)) continue;
            // The current output counts toward the quota, but cannot be deleted.
            if (string.Equals(file.FullName, protectedFullPath, StringComparison.OrdinalIgnoreCase))
                continue;
            var overQuota = totalBytes > quotaBytes;
            var lowDisk = drive != null && drive.AvailableFreeSpace < minimumFreeBytes;
            if (!overQuota && !lowDisk)
                break;

            try
            {
                var length = file.Length;
                file.Delete();
                totalBytes -= length;
                drive = root == null ? null : new DriveInfo(root);
            }
            catch (IOException)
            {
                // A file may be open in a media player. Continue with the next
                // oldest file instead of failing a successful export.
            }
            catch (UnauthorizedAccessException)
            {
                // Same policy for files that cannot be removed due to ACLs.
            }
        }
    }

    private List<FileInfo> ManagedVideos(string outputDirectory)
    {
        lock (RenameGate)
        {
            Dictionary<string, VideoStamp> index;
            try { index = ReadRenameIndex(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { index = new(StringComparer.OrdinalIgnoreCase); }

            return new DirectoryInfo(outputDirectory).EnumerateFiles("*.mp4", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Where(file => !file.Name.EndsWith(".part.mp4", StringComparison.OrdinalIgnoreCase) && IsManaged(file, index)).ToList();
        }
    }

    private Dictionary<string, VideoStamp> ReadRenameIndex()
    {
        var entries = File.Exists(_renameIndex)
            ? JsonSerializer.Deserialize<Dictionary<string, VideoStamp>>(File.ReadAllText(_renameIndex))
            : null;
        return entries == null ? new(StringComparer.OrdinalIgnoreCase) : new(entries, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsManaged(FileInfo file, Dictionary<string, VideoStamp> index) =>
        HasRecordingPrefix(file.Name) || (index.TryGetValue(file.FullName, out var stamp) && file.Exists && stamp == VideoStamp.From(file));

    private static bool HasRecordingPrefix(string name) =>
        name.StartsWith("highlight_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("fullgame_", StringComparison.OrdinalIgnoreCase);

    private readonly record struct VideoStamp(long Length, DateTime LastWriteUtc)
    {
        public static VideoStamp From(FileInfo file) => new(file.Length, file.LastWriteTimeUtc);
    }

    private static string RecordingRoot(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current != null; current = current.Parent)
            if (current.Name.Equals(".pending", StringComparison.OrdinalIgnoreCase) && current.Parent != null) return current.Parent.FullName;
        return directory;
    }
}
