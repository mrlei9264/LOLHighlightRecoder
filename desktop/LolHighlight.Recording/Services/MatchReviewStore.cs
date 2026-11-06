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
    {
        var batches = new List<MatchReviewBatch>();
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
                if (Files(batch).Count > 0) batches.Add(batch);
                else Cleanup(batch);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        }
        return batches.OrderBy(b => b.CreatedAt).ToArray();
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
        if (!Directory.Exists(batch.Directory)) return [];
        return Directory.EnumerateFiles(batch.Directory, "*.mp4")
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
        Directory.CreateDirectory(batch.Destination);
        // Move selected videos first. If a move fails, remaining candidates are retained for recovery.
        foreach (var file in files.Where(chosen.Contains))
        {
            var destination = Path.Combine(batch.Destination, Path.GetFileName(file));
            if (File.Exists(destination)) destination = Path.Combine(batch.Destination, Path.GetFileNameWithoutExtension(file) + "_" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
            File.Move(file, destination, false);
        }
        foreach (var file in files.Where(f => !chosen.Contains(f))) File.Delete(file);
        Cleanup(batch);
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
    }
    private static void Cleanup(MatchReviewBatch batch)
    {
        Validate(batch);
        if (!Directory.Exists(batch.Directory)) return;
        if (Directory.EnumerateFiles(batch.Directory).Any(p => !Path.GetFileName(p).Equals("batch.json", StringComparison.OrdinalIgnoreCase))) return;
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
