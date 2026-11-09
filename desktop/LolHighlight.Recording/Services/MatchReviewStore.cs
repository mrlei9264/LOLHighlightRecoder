using System.Diagnostics;
using System.Text.Json;
using LolHighlight.Recording.Infrastructure;

namespace LolHighlight.Recording.Services;

public sealed record MatchReviewBatch(string Id, string Directory, string Destination, DateTime CreatedAt, int OwnerProcessId, bool Ready);

/// <summary>New recordings stay in an owned pending folder until the user chooses what to keep.</summary>
public sealed class MatchReviewStore(AppConfig config, AppPaths paths)
{
    private readonly object _gate = new();
    private MatchReviewBatch? _active;
    private readonly HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);
    private bool _rootsLoaded;
    public event EventHandler? Ready;
    private string Root => Path.Combine(paths.ResolveOutputDirectory(config.OutputDir), ".pending");

    public void BeginMatch()
    {
        lock (_gate)
        {
            if (_active != null || !config.ReviewAfterMatch) return;
            var id = Guid.NewGuid().ToString("N");
            LoadRoots(); _roots.Add(Root);
            var index = Path.Combine(paths.DataRoot, "match-review-roots.json");
            File.WriteAllText(index + ".tmp", JsonSerializer.Serialize(_roots.ToArray()));
            File.Move(index + ".tmp", index, true);
            var folder = Path.Combine(Root, id);
            Directory.CreateDirectory(folder);
            _active = new(id, folder, paths.ResolveOutputDirectory(config.OutputDir), DateTime.Now, Environment.ProcessId, false);
            Save(_active);
        }
    }

    public string OutputDirectory
    {
        get { lock (_gate) return _active?.Directory ?? paths.ResolveOutputDirectory(config.OutputDir); }
    }

    public void CompleteMatch()
    {
        lock (_gate)
        {
            if (_active == null) return;
            var batch = _active with { Ready = true };
            Save(batch); _active = null;
        }
        // UI failures cannot fail a successful recording.
        foreach (EventHandler handler in Ready?.GetInvocationList() ?? [])
            try { handler(this, EventArgs.Empty); } catch (Exception) { }
    }

    public IReadOnlyList<MatchReviewBatch> Pending()
        => Batches(discarded: false).Select(item => item.Batch).OrderBy(batch => batch.CreatedAt).ToArray();

    public IReadOnlyList<(MatchReviewBatch Batch, int Count)> Discarded()
        => Batches(discarded: true).OrderByDescending(item => item.Batch.CreatedAt).ToArray();

    private IReadOnlyList<(MatchReviewBatch Batch, int Count)> Batches(bool discarded)
    {
        var batches = new List<(MatchReviewBatch Batch, int Count)>();
        string[] roots;
        lock (_gate) { LoadRoots(); roots = _roots.Append(Root).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
        foreach (var root in roots)
        {
        string[] folders;
        try { folders = Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
        foreach (var folder in folders)
        {
            try
            {
                var batch = JsonSerializer.Deserialize<MatchReviewBatch>(File.ReadAllText(Path.Combine(folder, "batch.json")));
                if (batch == null || !Path.GetFullPath(folder).Equals(Path.GetFullPath(batch.Directory), StringComparison.OrdinalIgnoreCase)) continue;
                lock (_gate) { if (_active?.Id == batch.Id) continue; }
                if (!batch.Ready && IsAlive(batch.OwnerProcessId)) continue;
                var count = (discarded ? DiscardedFiles(batch) : Files(batch)).Count;
                if (count > 0) batches.Add((batch, count));
                else Cleanup(batch);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        }
        return batches;
    }

    private void LoadRoots()
    {
        if (_rootsLoaded) return;
        _rootsLoaded = true;
        try
        {
            var file = Path.Combine(paths.DataRoot, "match-review-roots.json");
            if (File.Exists(file)) foreach (var root in JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? [])
                if (Path.IsPathFullyQualified(root) && Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) == ".pending") _roots.Add(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    public IReadOnlyList<string> Files(MatchReviewBatch batch)
    {
        Validate(batch);
        return CompletedVideos(batch.Directory);
    }

    public IReadOnlyList<string> DiscardedFiles(MatchReviewBatch batch)
    {
        Validate(batch);
        return CompletedVideos(DiscardDirectory(batch));
    }

    private static IReadOnlyList<string> CompletedVideos(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.mp4", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => !f.EndsWith(".part.mp4", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .OrderBy(File.GetLastWriteTimeUtc).ToArray();
    }

    public void Apply(MatchReviewBatch batch, IEnumerable<string> keep)
    {
        Validate(batch);
        var files = Files(batch);
        var chosen = keep.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (chosen.Any(p => !files.Contains(p, StringComparer.OrdinalIgnoreCase))) throw new IOException("待保存录像已发生变化，请重新选择。");
        var discarded = files.Where(file => !chosen.Contains(file)).ToArray();
        var discardDirectory = DiscardDirectory(batch);
        // Move selected videos first. If a move fails, remaining candidates are retained for recovery.
        foreach (var file in files.Where(chosen.Contains))
            MoveVideo(file, batch.Destination);
        foreach (var file in discarded)
            MoveVideo(file, discardDirectory);
        Cleanup(batch);
    }

    public void RestoreDiscarded(MatchReviewBatch batch)
    {
        foreach (var file in DiscardedFiles(batch))
            MoveVideo(file, batch.Destination);
        Cleanup(batch);
    }

    public void PurgeDiscarded(MatchReviewBatch batch, IReadOnlyList<string> files)
    {
        var available = DiscardedFiles(batch).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (files.Any(file => !available.Contains(file)))
            throw new IOException("已丢弃录像发生变化，请重新选择。");
        foreach (var file in files)
            File.Delete(file);
        Cleanup(batch);
    }

    private static void MoveVideo(string file, string directory)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(file));
        if (File.Exists(destination))
            destination = Path.Combine(directory, Path.GetFileNameWithoutExtension(file) + "_" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
        File.Move(file, destination, overwrite: false);
    }

    private static string DiscardDirectory(MatchReviewBatch batch)
    {
        var directory = Path.Combine(batch.Directory, ".discarded");
        CheckOwnedDirectory(directory);
        return directory;
    }

    private static void CheckOwnedDirectory(string directory)
    {
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("待确认录像目录不能是重解析点。");
    }

    private static void Save(MatchReviewBatch batch)
    {
        var file = Path.Combine(batch.Directory, "batch.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(batch));
        File.Move(file + ".tmp", file, true);
    }
    private static void Validate(MatchReviewBatch batch)
    {
        if (!Guid.TryParseExact(batch.Id, "N", out _) ||
            !Path.GetFullPath(batch.Directory).Equals(Path.GetFullPath(Path.Combine(batch.Destination, ".pending", batch.Id)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("待确认录像目录无效。");
        CheckOwnedDirectory(Path.Combine(batch.Destination, ".pending"));
        CheckOwnedDirectory(batch.Directory);
    }
    private static void Cleanup(MatchReviewBatch batch)
    {
        Validate(batch);
        if (!Directory.Exists(batch.Directory)) return;
        var discarded = DiscardDirectory(batch);
        if (Directory.Exists(discarded) && !Directory.EnumerateFileSystemEntries(discarded).Any())
            Directory.Delete(discarded);
        if (Directory.EnumerateFileSystemEntries(batch.Directory).Any(p => !Path.GetFileName(p).Equals("batch.json", StringComparison.OrdinalIgnoreCase))) return;
        File.Delete(Path.Combine(batch.Directory, "batch.json"));
        Directory.Delete(batch.Directory, false);
    }
    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }
}
