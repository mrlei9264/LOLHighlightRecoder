namespace LolHighlight.Recording.Services;

public interface IStorageQuotaManager
{
    void EnsureRoom(string outputDirectory, int quotaGb, int minimumFreeGb, string? protectedPath = null, long incomingBytes = 0);
    void Enforce(string outputDirectory, int quotaGb, int minimumFreeGb, string? protectedPath = null, long incomingBytes = 0);
}

public sealed class StorageQuotaManager : IStorageQuotaManager
{
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
        var total = new DirectoryInfo(outputDirectory).EnumerateFiles("*.mp4", new EnumerationOptions
        { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => (f.Name.StartsWith("highlight_", StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith("fullgame_", StringComparison.OrdinalIgnoreCase)) &&
                !f.Name.EndsWith(".part.mp4", StringComparison.OrdinalIgnoreCase)).Sum(f => f.Length);
        if (total + incomingBytes > quotaGb * 1024L * 1024 * 1024)
            throw new IOException("录像空间已达上限，请先处理待确认录像或提高容量上限。");

        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outputDirectory))!);
        if (drive.AvailableFreeSpace < minimumFreeGb * 1024L * 1024L * 1024L + incomingBytes)
            throw new IOException($"Not enough free disk space on {drive.Name}");
    }

    public void Enforce(string outputDirectory, int quotaGb, int minimumFreeGb, string? protectedPath = null, long incomingBytes = 0)
    {
        outputDirectory = RecordingRoot(outputDirectory);
        if (!Directory.Exists(outputDirectory))
            return;

        var protectedFullPath = protectedPath == null
            ? null
            : Path.GetFullPath(protectedPath);

        var files = new DirectoryInfo(outputDirectory)
            .EnumerateFiles("*.mp4", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            })
            .Where(file => (file.Name.StartsWith("highlight_", StringComparison.OrdinalIgnoreCase) ||
                            file.Name.StartsWith("fullgame_", StringComparison.OrdinalIgnoreCase)) &&
                           !file.Name.EndsWith(".part.mp4", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();

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

    private static string RecordingRoot(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current != null; current = current.Parent)
            if (current.Name.Equals(".pending", StringComparison.OrdinalIgnoreCase) && current.Parent != null) return current.Parent.FullName;
        return directory;
    }
}
