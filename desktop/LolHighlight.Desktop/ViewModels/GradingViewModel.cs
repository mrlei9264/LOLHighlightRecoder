using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using LolHighlight.Desktop.Services;

namespace LolHighlight.Desktop.ViewModels;

public abstract class GradingObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
}

public sealed class GradingSource(string path, string? title = null, string tags = "", string meta = "") : GradingObservable
{
    private string _name = title ?? System.IO.Path.GetFileName(path), _tags = tags, _meta = meta;
    public string Path { get; } = path;
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string TagText { get => _tags; private set => Set(ref _tags, value); }
    public string Meta { get => _meta; private set => Set(ref _meta, value); }
    public void UpdateFrom(GradingSource source) { Name = source.Name; TagText = source.TagText; Meta = source.Meta; }
}

public sealed class GradingViewModel : GradingObservable, IDisposable
{
    private readonly IColorGradingService _grading;
    private readonly IVideoScanner _scanner;
    private readonly IColorPickerService _picker;
    private readonly AppPaths _paths;
    private readonly AppConfig _config;
    private readonly IMetadataFooterService _footer;
    private readonly DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _cancel;
    private Task _activeTask = Task.CompletedTask;
    private bool _busy, _disposed;
    private ColorTone _tone = ColorTones.All[0];
    private GradingSource? _source;
    private double _strength = .8, _progress;
    private string _message = "", _preview = "", _output, _lastOutput = "";
    private bool _askExportLocation = true, _compareOriginal;
    private int _scanVersion;
    private bool _updatingSources;

    public GradingViewModel(IColorGradingService grading, IVideoScanner scanner, IColorPickerService picker, AppPaths paths, AppConfig config, IMetadataFooterService footer)
    {
        _grading = grading; _scanner = scanner; _picker = picker; _paths = paths; _config = config;
        _footer = footer;
        try { _dispatcher = DispatcherQueue.GetForCurrentThread(); } catch { }
        _output = Path.Combine(paths.DataRoot, "graded");
        LoadPreferences();
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GenerateCommand = new AsyncRelayCommand(StartAsync);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel());
        FolderCommand = new AsyncRelayCommand(ChooseFolderAsync);
        OriginalCommand = new RelayCommand(() => CompareOriginal = !CompareOriginal);
        RevealCommand = new RelayCommand(() => { if (File.Exists(_lastOutput)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true }); });
    }
    public ObservableCollection<GradingSource> Sources { get; } = new();
    public IReadOnlyList<ColorTone> Tones => ColorTones.All;
    public ColorTone SelectedTone { get => _tone; set { if (value != null && Set(ref _tone, value)) { Changed(nameof(SelectionHint)); CompareOriginal = false; } } }
    public double Strength { get => _strength; set { if (Set(ref _strength, Math.Clamp(value, 0, 1))) { Changed(nameof(StrengthText)); CompareOriginal = false; } } }
    public bool CompareOriginal { get => _compareOriginal; set { if (Set(ref _compareOriginal, value)) Changed(nameof(CompareLabel)); } }
    public string CompareLabel => CompareOriginal ? "返回实时调色" : "对比原片";
    public bool AskExportLocation { get => _askExportLocation; set => Set(ref _askExportLocation, value); }
    public string StrengthText => $"调色强度 · {Strength:P0}";
    public string SelectionHint => $"{SelectedTone.Name} · {SelectedTone.Description}";
    public GradingSource? SelectedSource { get => _source; set { if (value == null && _updatingSources) return; if (Set(ref _source, value)) { ShowOriginal(); Changed(nameof(HasSource)); Changed(nameof(CanGenerate)); } } }
    public bool HasSource => SelectedSource != null;
    public string PreviewPath { get => _preview; private set => Set(ref _preview, value); }
    public string OutputDirectory { get => _output; set => Set(ref _output, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { Changed(nameof(CanChange)); Changed(nameof(CanGenerate)); Changed(nameof(GenerateLabel)); } } }
    public bool CanChange => !IsBusy;
    public bool CanGenerate => !IsBusy && HasSource;
    public string GenerateLabel => IsBusy ? $"生成 {Progress:0}%" : "生成";
    public double Progress { get => _progress; private set { if (Set(ref _progress, value)) Changed(nameof(GenerateLabel)); } }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string SourceSummary => $"{Sources.Count} 个录像";
    public bool HasOutput => File.Exists(_lastOutput);
    public ICommand RefreshCommand { get; }
    public ICommand GenerateCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand FolderCommand { get; }
    public ICommand OriginalCommand { get; }
    public ICommand RevealCommand { get; }

    public void AddSources(IEnumerable<string> paths)
    {
        GradingSource? first = null;
        foreach (var path in paths.Where(File.Exists))
        {
            var full = Path.GetFullPath(path);
            var item = Sources.FirstOrDefault(s => s.Path.Equals(full, StringComparison.OrdinalIgnoreCase));
            if (item != null) { first ??= item; continue; }
            item = new(full, tags: VideoTags.Read(_footer, full));
            Sources.Add(item); first ??= item;
        }
        if (first != null) SelectedSource = first;
        Changed(nameof(SourceSummary));
    }
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        var version = ++_scanVersion;
        var directory = _paths.ResolveOutputDirectory(_config.OutputDir);
        try
        {
            var items = await Task.Run(() => _scanner.ScanVideos(directory)
                .Select(v => new GradingSource(v.Path, v.DisplayName, VideoTags.Read(_footer, v.Path), $"{v.MetaText} · {v.SizeText}")).ToArray());
            if (_disposed || version != _scanVersion || directory != _paths.ResolveOutputDirectory(_config.OutputDir)) return;
            var selected = SelectedSource?.Path;
            var imported = Sources.Where(s => !items.Any(v => v.Path.Equals(s.Path, StringComparison.OrdinalIgnoreCase)) && File.Exists(s.Path)).ToArray();
            var desired = items.Concat(imported).ToArray();
            _updatingSources = true;
            try
            {
            foreach (var old in Sources.Where(s => !desired.Any(v => v.Path.Equals(s.Path, StringComparison.OrdinalIgnoreCase))).ToArray()) Sources.Remove(old);
            for (var i = 0; i < desired.Length; i++)
            {
                var existing = Sources.FirstOrDefault(s => s.Path.Equals(desired[i].Path, StringComparison.OrdinalIgnoreCase));
                if (existing == null) Sources.Insert(i, desired[i]);
                else { existing.UpdateFrom(desired[i]); var previous = Sources.IndexOf(existing); if (previous != i) Sources.Move(previous, i); }
            }
            }
            finally { _updatingSources = false; }
            SelectedSource = Sources.FirstOrDefault(s => s.Path.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? Sources.FirstOrDefault();
            Changed(nameof(SourceSummary));
        }
        catch (Exception e) { Message = "读取录像失败：" + e.Message; }
    }
    private async Task ChooseFolderAsync() { if (IsBusy) return; try { var folder = await _picker.PickFolderAsync(); if (folder != null) OutputDirectory = folder; } catch (Exception e) { Message = e.Message; } }
    private void ShowOriginal() => PreviewPath = SelectedSource?.Path ?? "";
    public void ShowError(string message) => Message = message;
    private Task StartAsync()
    {
        if (IsBusy) return Task.CompletedTask;
        _activeTask = GenerateAsync();
        return _activeTask;
    }
    private async Task GenerateAsync()
    {
        IsBusy = true; Progress = 0;
        _cancel = new();
        var token = _cancel.Token;
        try
        {
            var sources = SelectedSource == null ? Array.Empty<GradingSource>() : new[] { SelectedSource };
            if (sources.Length == 0) throw new ArgumentException("请选择录像。");
            var tone = SelectedTone; var strength = Strength;
            if (AskExportLocation)
            {
                var destination = await _picker.PickExportDestinationAsync(OutputDirectory);
                if (destination == null) { Message = "已取消导出。"; return; }
                OutputDirectory = destination.Folder;
                AskExportLocation = !destination.Remember;
            }
            token.ThrowIfCancellationRequested();
            var folder = Path.GetFullPath(OutputDirectory);
            SavePreferences();
            for (var i = 0; i < sources.Length; i++)
            {
                var index = i;
                var output = Path.Combine(folder, $"grade_{tone.Id}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.mp4");
                var progress = new Progress<ColorGradeProgress>(p => Dispatch(() =>
                { if (!_disposed) { Progress = (index + p.Percent / 100) / sources.Length * 100; Message = p.Message; } }));
                var result = await _grading.RenderAsync(new(sources[i].Path, output, tone.Id, strength), progress, token);
                _lastOutput = result; Changed(nameof(HasOutput));
            }
            Message = "已生成";
            Progress = 100;
        }
        catch (OperationCanceledException) { Message = "已取消"; }
        catch (Exception e) { Message = e.Message; }
        finally { _cancel.Dispose(); _cancel = null; IsBusy = false; }
    }
    private void Dispatch(Action action) { if (_dispatcher is { HasThreadAccess: false }) _dispatcher.TryEnqueue(() => action()); else action(); }
    private string PreferencesPath => Path.Combine(_paths.DataRoot, "color-grading.json");
    private void SavePreferences()
    {
        Directory.CreateDirectory(_paths.DataRoot);
        var temp = PreferencesPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Preferences(SelectedTone.Id, Strength, OutputDirectory, AskExportLocation)));
        File.Move(temp, PreferencesPath, true);
    }
    private void LoadPreferences()
    {
        try
        {
            if (!File.Exists(PreferencesPath)) return;
            var p = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesPath));
            if (p == null) return;
            _tone = ColorTones.All.FirstOrDefault(t => t.Id == p.Tone) ?? ColorTones.All[0];
            _strength = double.IsFinite(p.Strength) ? Math.Clamp(p.Strength, 0, 1) : .8;
            if (!string.IsNullOrWhiteSpace(p.Output)) _output = p.Output;
            _askExportLocation = p.AskExportLocation;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
    private sealed record Preferences(string Tone, double Strength, string Output, bool AskExportLocation = true);
    public async Task StopAsync() { _cancel?.Cancel(); await _activeTask; }
    public void Dispose()
    {
        _disposed = true; _cancel?.Cancel();
        try { SavePreferences(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
