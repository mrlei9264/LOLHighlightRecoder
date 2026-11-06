namespace LolHighlight.Recording.Infrastructure;

internal static class RuntimeDiagnostics
{
    private static readonly object Gate = new();

    internal static void Write(string directory, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "listener.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void PruneTelemetry(string directory)
    {
        if (!Directory.Exists(directory)) return;
        // Remove only recorder-owned telemetry, never an arbitrary configured directory.
        foreach (var session in new DirectoryInfo(directory).EnumerateDirectories("session_*"))
        {
            if (session.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7) ||
                (session.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                foreach (var file in session.EnumerateFiles("telemetry.ndjson*")) file.Delete();
                if (!session.EnumerateFileSystemInfos().Any()) session.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
