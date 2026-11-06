namespace LolHighlight.Recording.Infrastructure;

/// <summary>
/// Owns every user-writable path used by the application.  Keeping these paths
/// in one place prevents the UI, recorder and library from resolving relative
/// paths against different working directories.
/// </summary>
public sealed class AppPaths
{
    public string DataRoot { get; }
    public string ConfigPath => Path.Combine(DataRoot, "config.json");
    public string ExampleConfigPath => Path.Combine(DataRoot, "config.example.json");
    public string OutputDirectory { get; }
    public string RuntimeDirectory { get; }

    public AppPaths(string? programDirectory = null)
    {
        var baseDirectory = programDirectory ?? AppContext.BaseDirectory;
        DataRoot = programDirectory is null && File.Exists(Path.Combine(baseDirectory, "installed-mode"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiftReplay")
            : Path.GetFullPath(baseDirectory);
        OutputDirectory = Path.Combine(DataRoot, "recordings");
        RuntimeDirectory = DataRoot;
    }

    public bool IsInsideProgramDirectory(string path)
    {
        var full = Path.GetFullPath(path, DataRoot);
        return Path.TrimEndingDirectorySeparator(full).Equals(Path.TrimEndingDirectorySeparator(DataRoot), StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(Path.TrimEndingDirectorySeparator(DataRoot) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    public void NormalizeDataDirectories(LolHighlight.Recording.Models.AppConfig config)
    {
        if (!IsInsideProgramDirectory(config.RuntimeDir)) config.RuntimeDir = ".runtime";
    }

    public string ResolveOutputDirectory(string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, DataRoot);
    }

    public string ResolveRuntimeDirectory(string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, DataRoot);
    }
}
