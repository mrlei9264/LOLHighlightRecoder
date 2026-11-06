namespace LolHighlight.Recording.Infrastructure;

internal static class FfmpegExecutable
{
    public static string Resolve(string configuredPath, string? baseDirectory = null)
    {
        var path = configuredPath.Trim();
        if (path.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) || path.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            var bundled = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(bundled)) return bundled;
        }
        return path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(path, baseDirectory ?? AppContext.BaseDirectory)
            : path;
    }
}
