using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.ViewModels;

public class LibraryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IVideoScanner _videoScanner;
    private readonly IMetadataFooterService _metadataFooterService;
    private readonly AppPaths _paths;
    private readonly AppConfig _config;
    private readonly List<VideoItem> _allVideos = new();

    private VideoItem? _selectedVideo;
    private string _searchQuery = "";
    private bool _hasSelection;
    private bool _hasVideos;
    private bool _hasMetadata;
    private bool _hasMetadataMessage;
    private string _metadataMessage = "";
    private string _selectedTitle = "";
    private string _selectedPlayer = "";
    private string _selectedRecordedAt = "";
    private string _selectedClipInfo = "";
    private string _selectedFileInfo = "";
    private string _statusMessage = "";
    private bool _hasStatusMessage;
    private bool _isStatusError;
    private bool _isRefreshing;
    private bool _disposed;
    private int _detailsVersion;
    private Task _detailsTask = Task.CompletedTask;
    private readonly Dictionary<string, string[]> _events = new(StringComparer.OrdinalIgnoreCase);
    private string _eventFilter = "all", _sort = "newest";
    private DateTimeOffset? _dateFrom, _dateTo;
    private int _scanVersion;
    public DateTimeOffset LibraryDateMin { get; private set; } = DateTimeOffset.Now.Date;
    public DateTimeOffset LibraryDateMax { get; private set; } = DateTimeOffset.Now.Date;

    private void UpdateDateRange()
    {
        LibraryDateMin = _allVideos.Count == 0 ? DateTimeOffset.Now.Date : new DateTimeOffset(_allVideos.Min(v => v.CreatedAt).Date);
        LibraryDateMax = _allVideos.Count == 0 ? DateTimeOffset.Now.Date : new DateTimeOffset(_allVideos.Max(v => v.CreatedAt).Date);
        _dateFrom = _dateFrom == null || _dateFrom < LibraryDateMin || _dateFrom > LibraryDateMax ? LibraryDateMin : _dateFrom;
        _dateTo = _dateTo == null || _dateTo > LibraryDateMax || _dateTo < LibraryDateMin ? LibraryDateMax : _dateTo;
        OnPropertyChanged(nameof(LibraryDateMin)); OnPropertyChanged(nameof(LibraryDateMax));
        OnPropertyChanged(nameof(DateFrom)); OnPropertyChanged(nameof(DateTo));
    }

    public IReadOnlyList<SettingsChoice<string>> EventFilterOptions { get; } =
        new[] { new SettingsChoice<string>("all", "全部事件") }.Concat(RecordingEventCatalog.All.Select(e => new SettingsChoice<string>(e.Id, e.Label))).ToArray();
    public IReadOnlyList<SettingsChoice<string>> SortOptions { get; } =
        [new("newest", "日期：从新到旧"), new("oldest", "日期：从旧到新"), new("event", "事件：多杀优先")];
    public string EventFilter { get => _eventFilter; set { if (_eventFilter == value) return; _eventFilter = value; OnPropertyChanged(); ApplyFilter(SelectedVideo?.Path); } }
    public string SortOrder { get => _sort; set { if (_sort == value) return; _sort = value; OnPropertyChanged(); ApplyFilter(SelectedVideo?.Path); } }
    public DateTimeOffset? DateFrom { get => _dateFrom; set { _dateFrom = value; OnPropertyChanged(); ApplyFilter(SelectedVideo?.Path); } }
    public DateTimeOffset? DateTo { get => _dateTo; set { _dateTo = value; OnPropertyChanged(); ApplyFilter(SelectedVideo?.Path); } }
    public ObservableRangeCollection<LibraryVideoCard> Cards { get; } = new();
    public LibraryVideoCard? SelectedCard { get => Cards.FirstOrDefault(c => c.Video == SelectedVideo); set { if (value != null) SelectedVideo = value.Video; } }
    public void ResetFilters() { _eventFilter = "all"; _sort = "newest"; _dateFrom = LibraryDateMin; _dateTo = LibraryDateMax; _searchQuery = ""; foreach (var p in new[] { nameof(EventFilter), nameof(SortOrder), nameof(DateFrom), nameof(DateTo), nameof(SearchQuery) }) OnPropertyChanged(p); ApplyFilter(SelectedVideo?.Path); }

    public LibraryViewModel(
        IVideoScanner videoScanner,
        IMetadataFooterService metadataFooterService,
        AppPaths paths,
        AppConfig config)
    {
        _videoScanner = videoScanner;
        _metadataFooterService = metadataFooterService;
        _paths = paths;
        _config = config;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PlayCommand = new RelayCommand(PlaySelected);
        RevealCommand = new RelayCommand(RevealSelected);
        DeleteCommand = new RelayCommand(DeleteSelected);
        ExportTelemetryCommand = new AsyncRelayCommand(ExportTelemetryAsync);

    }

    public VideoItem? SelectedVideo
    {
        get => _selectedVideo;
        set
        {
            if (Equals(_selectedVideo, value))
                return;

            _selectedVideo = value;
            HasSelection = value != null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCard));
            _detailsTask = LoadDetailsAsync(value);
        }
    }

    public bool HasSelection { get => _hasSelection; private set => SetField(ref _hasSelection, value); }
    public bool HasVideos { get => _hasVideos; private set => SetField(ref _hasVideos, value); }
    public bool HasMetadata { get => _hasMetadata; private set => SetField(ref _hasMetadata, value); }
    public bool HasMetadataMessage { get => _hasMetadataMessage; private set => SetField(ref _hasMetadataMessage, value); }

    public string MetadataMessage { get => _metadataMessage; private set => SetField(ref _metadataMessage, value); }
    public string SelectedTitle { get => _selectedTitle; private set => SetField(ref _selectedTitle, value); }
    public string SelectedPlayer { get => _selectedPlayer; private set => SetField(ref _selectedPlayer, value); }
    public string SelectedRecordedAt { get => _selectedRecordedAt; private set => SetField(ref _selectedRecordedAt, value); }
    public string SelectedClipInfo { get => _selectedClipInfo; private set => SetField(ref _selectedClipInfo, value); }
    public string SelectedFileInfo { get => _selectedFileInfo; private set => SetField(ref _selectedFileInfo, value); }
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }
    public bool HasStatusMessage { get => _hasStatusMessage; private set => SetField(ref _hasStatusMessage, value); }
    public bool IsStatusError { get => _isStatusError; private set => SetField(ref _isStatusError, value); }
    public bool IsRefreshing { get => _isRefreshing; private set => SetField(ref _isRefreshing, value); }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value)
                return;
            _searchQuery = value;
            OnPropertyChanged();
            ApplyFilter(SelectedVideo?.Path);
        }
    }

    public string CountText => _allVideos.Count == 0
        ? "暂无录像"
        : string.IsNullOrWhiteSpace(SearchQuery)
            ? $"{_allVideos.Count} 个片段"
            : $"{Videos.Count} / {_allVideos.Count} 个片段";

    public string EmptyText => IsRefreshing ? "正在扫描录像…" : _allVideos.Count == 0
        ? "暂无录像"
        : "没有匹配的录像";

    public ObservableRangeCollection<VideoItem> Videos { get; } = new();
    public ObservableCollection<string> SelectedTriggers { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ExportTelemetryCommand { get; }

    private async Task ExportTelemetryAsync()
    {
        var video = SelectedVideo;
        if (video == null || _disposed) return;
        var path = Path.Combine(Path.GetDirectoryName(video.Path)!,
            $"{Path.GetFileNameWithoutExtension(video.Path)}.data_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.ndjson");
        try
        {
            var hasData = await Task.Run(async () =>
            {
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true);
                await TelemetryArchive.ExtractAsync(video.Path, output);
                if (output.Length == 0 && _metadataFooterService.ReadMetadataFooter(video.Path)?.GetValueOrDefault("telemetry") is IEnumerable<object> legacy)
                {
                    foreach (var record in legacy)
                        await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(record) + "\n"));
                }
                return output.Length > 0;
            });
            if (!hasData) File.Delete(path);
            if (!_disposed) ShowStatus(hasData ? $"对局数据已导出到录像所在文件夹：{Path.GetFileName(path)}" : "该录像没有可导出的对局数据。", false);
        }
        catch (Exception error)
        {
            try { File.Delete(path); } catch { }
            if (!_disposed) ShowStatus($"导出对局数据失败：{error.Message}", true);
        }
    }

    public void Refresh()
    {
        var selectedPath = SelectedVideo?.Path;
        var outputDirectory = _paths.ResolveOutputDirectory(_config.OutputDir);

        _allVideos.Clear();
        _allVideos.AddRange(_videoScanner.ScanVideos(outputDirectory));
        UpdateDateRange();
        ApplyFilter(selectedPath);
    }

    public async Task RefreshAsync()
    {
        if (_disposed || IsRefreshing) return;
        IsRefreshing = true;
        var version = ++_scanVersion;
        OnPropertyChanged(nameof(EmptyText));
        var directory = _paths.ResolveOutputDirectory(_config.OutputDir);
        try
        {
            var videos = await Task.Run(() => _videoScanner.ScanVideos(directory));
            if (_disposed || directory != _paths.ResolveOutputDirectory(_config.OutputDir)) return;
            var selectedPath = SelectedVideo?.Path;
            _allVideos.Clear();
            _allVideos.AddRange(videos);
            UpdateDateRange();
            ApplyFilter(selectedPath);
            var indexed = await Task.Run(() => videos.ToDictionary(v => v.Path, v => ReadEventIds(v.Path), StringComparer.OrdinalIgnoreCase));
            if (_disposed || version != _scanVersion) return;
            foreach (var entry in indexed) _events[entry.Key] = entry.Value;
            ApplyFilter(SelectedVideo?.Path);
            await _detailsTask;
        }
        catch (Exception error) { if (!_disposed) ShowStatus($"无法读取录像目录：{error.Message}", true); }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    public void ReportPlaybackError(string message) => ShowStatus($"预览失败：{message}", true);

    public void Dispose()
    {
        _disposed = true;
        _detailsVersion++;
    }

    public void ClearStatus()
    {
        HasStatusMessage = false;
        StatusMessage = "";
    }

    public void DeleteSelected()
    {
        var video = SelectedVideo;
        if (video == null)
            return;

        try
        {
            if (File.Exists(video.Path))
                File.Delete(video.Path);

            _allVideos.RemoveAll(item => string.Equals(item.Path, video.Path, StringComparison.OrdinalIgnoreCase));
            UpdateDateRange();
            ApplyFilter(null);
            ShowStatus("已删除该录像", false);
        }
        catch (IOException)
        {
            ShowStatus("录像正在被播放器占用，请先关闭播放器再删除。", true);
        }
        catch (UnauthorizedAccessException)
        {
            ShowStatus("没有权限删除该文件。", true);
        }
        catch (Exception exception)
        {
            ShowStatus($"删除失败：{exception.Message}", true);
        }
    }

    private void ApplyFilter(string? selectedPath)
    {
        var query = SearchQuery.Trim();
        var filtered = _allVideos
            .Where(video => query.Length == 0 ||
                            video.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            video.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            string.Join(' ', EventLabels(video.Path)).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(v => EventFilter == "all" || (_events.TryGetValue(v.Path, out var ids) && ids.Contains(EventFilter)))
            .Where(v => DateFrom == null || v.CreatedAt.Date >= DateFrom.Value.Date)
            .Where(v => DateTo == null || v.CreatedAt.Date <= DateTo.Value.Date);
        var ordered = SortOrder switch
        {
            "oldest" => filtered.OrderBy(v => v.CreatedAt),
            "event" => filtered.OrderByDescending(v => EventPriority(v.Path)).ThenByDescending(v => v.CreatedAt),
            _ => filtered.OrderByDescending(v => v.CreatedAt)
        };

        Videos.ReplaceAll(ordered.ToList());
        Cards.ReplaceAll(Videos.Select(v => new LibraryVideoCard(v, EventLabels(v.Path))).ToArray());

        HasVideos = Videos.Count > 0;
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyText));

        var keep = selectedPath == null
            ? null
            : Videos.FirstOrDefault(video => string.Equals(video.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        SelectedVideo = keep ?? Videos.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCard));
    }

    private string[] ReadEventIds(string path)
    {
        try
        {
            var m = _metadataFooterService.ReadMetadataFooter(path);
            return (m?.GetValueOrDefault("triggers") as IEnumerable<object> ?? Array.Empty<object>())
                .OfType<Dictionary<string, object>>().Select(t => t.GetValueOrDefault("kind")?.ToString() ?? "")
                .Where(t => t.Length > 0).Distinct().ToArray();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException) { return []; }
    }
    private string[] EventLabels(string path) => _events.TryGetValue(path, out var ids) && ids.Length > 0
        ? ids.Select(id => RecordingEventCatalog.All.FirstOrDefault(e => e.Id == id)?.Label ?? id).ToArray() : ["无事件标签"];
    private int EventPriority(string path) => !_events.TryGetValue(path, out var ids) ? 0 : ids.Select(id => id.StartsWith("multikill_") && int.TryParse(id.AsSpan(10), out var n) ? n * 10 : id == "kill" ? 5 : 1).DefaultIfEmpty(0).Max();

    private async Task LoadDetailsAsync(VideoItem? video)
    {
        var version = ++_detailsVersion;
        SelectedTriggers.Clear();
        HasMetadata = false;
        HasMetadataMessage = false;
        MetadataMessage = "";

        if (video == null)
        {
            SelectedTitle = "";
            SelectedPlayer = "";
            SelectedRecordedAt = "";
            SelectedClipInfo = "";
            SelectedFileInfo = "";
            return;
        }

        SelectedTitle = video.CreatedAt == default
            ? video.Name
            : $"{video.CreatedAt:MM 月 dd 日 HH:mm} 的高光";
        SelectedFileInfo = $"{video.Name} · {video.Summary} · {video.SizeText}";
        SelectedPlayer = "未知玩家";
        SelectedRecordedAt = video.CreatedAt == default ? "" : video.CreatedAt.ToString("yyyy-MM-dd HH:mm");
        SelectedClipInfo = "";

        try
        {
            var metadata = await Task.Run(() => _metadataFooterService.ReadMetadataFooter(video.Path));
            if (_disposed || version != _detailsVersion) return;
            if (metadata == null)
            {
                HasMetadataMessage = true;
                MetadataMessage = "该录像没有对局元数据。";
                return;
            }

            var playerName = ReadPlayerName(metadata);
            if (!string.IsNullOrWhiteSpace(playerName))
                SelectedPlayer = playerName;

            if (metadata.TryGetValue("recorded_at", out var recordedAtObj) &&
                DateTime.TryParse(recordedAtObj?.ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var recordedAt))
            {
                var local = recordedAt.Kind == DateTimeKind.Utc ? recordedAt.ToLocalTime() : recordedAt;
                SelectedRecordedAt = local.ToString("yyyy-MM-dd HH:mm");
            }

            ReadTriggers(metadata);
            if (metadata.GetValueOrDefault("type")?.ToString() == "full_game") SelectedTriggers.Add("整局连续录制");
            SelectedClipInfo = BuildClipInfo(metadata);
            if (metadata.GetValueOrDefault("partial_buffer") is true)
            {
                HasMetadataMessage = true;
                MetadataMessage = "录制刚开始或内存容量不足，此片段的回溯内容少于设定时长。";
            }
            HasMetadata = true;
        }
        catch (Exception exception)
        {
            if (_disposed || version != _detailsVersion) return;
            HasMetadataMessage = true;
            MetadataMessage = $"读取对局数据失败：{exception.Message}";
        }
    }

    private void ReadTriggers(Dictionary<string, object> metadata)
    {
        if (!metadata.TryGetValue("triggers", out var triggersObj) || triggersObj is not IEnumerable<object> triggers)
            return;

        foreach (var item in triggers)
        {
            if (item is not Dictionary<string, object> trigger)
                continue;

            var label = trigger.GetValueOrDefault("label")?.ToString();
            if (!string.IsNullOrWhiteSpace(label) && !SelectedTriggers.Contains(label))
                SelectedTriggers.Add(label);
        }
    }

    private static string BuildClipInfo(Dictionary<string, object> metadata)
    {
        if (!metadata.TryGetValue("clip_window", out var windowObj) ||
            windowObj is not Dictionary<string, object> window)
            return "";

        var start = ToDouble(window.GetValueOrDefault("start_seconds"));
        var end = ToDouble(window.GetValueOrDefault("end_seconds"));
        if (end <= start)
            return "";

        return $"片段时长 {end - start:0} 秒";
    }

    private static string ReadPlayerName(Dictionary<string, object> metadata)
    {
        if (!metadata.TryGetValue("player", out var playerObj) ||
            playerObj is not Dictionary<string, object> player)
            return "";

        var gameName = player.GetValueOrDefault("game_name")?.ToString() ?? "";
        var tagLine = player.GetValueOrDefault("tag_line")?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(gameName))
            return "";

        return string.IsNullOrWhiteSpace(tagLine) ? gameName : $"{gameName}#{tagLine}";
    }

    private void PlaySelected()
    {
        if (SelectedVideo == null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = SelectedVideo.Path, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowStatus($"无法打开视频：{exception.Message}", true);
        }
    }

    private void RevealSelected()
    {
        if (SelectedVideo == null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{SelectedVideo.Path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowStatus($"无法定位文件：{exception.Message}", true);
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
        HasStatusMessage = true;
    }

    private static double ToDouble(object? value)
    {
        if (value is double doubleValue) return doubleValue;
        if (value is float floatValue) return floatValue;
        if (value is int intValue) return intValue;
        if (value is long longValue) return longValue;
        return double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }
}

public sealed record LibraryVideoCard(VideoItem Video, string[] Tags)
{
    public string Path => Video.Path;
    public string Title => Video.DisplayName;
    public string Meta => $"{Video.MetaText} · {Video.SizeText}";
    public string TagText => string.Join("  ·  ", Tags.Take(4));
}
