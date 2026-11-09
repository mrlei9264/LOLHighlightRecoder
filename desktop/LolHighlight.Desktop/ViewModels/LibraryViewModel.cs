using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using LolHighlight.Desktop.Services;
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
    private readonly StorageQuotaManager _quota;
    private readonly List<VideoItem> _allVideos = new();
    private Dictionary<string, string> _titles;

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
    private string _eventFilter = "all", _sort = "newest", _datePreset = "all";
    private DateTimeOffset? _dateFrom, _dateTo;
    private int _scanVersion, _fileVersion;
    private bool _multiSelect, _updatingMarks;
    public DateTimeOffset LibraryDateMin { get; private set; } = DateTimeOffset.Now.Date;
    public DateTimeOffset LibraryDateMax { get; private set; } = DateTimeOffset.Now.Date;

    private void UpdateDateRange()
    {
        LibraryDateMin = _allVideos.Count == 0 ? DateTimeOffset.Now.Date : new DateTimeOffset(_allVideos.Min(v => v.CreatedAt).Date);
        LibraryDateMax = _allVideos.Count == 0 ? DateTimeOffset.Now.Date : new DateTimeOffset(_allVideos.Max(v => v.CreatedAt).Date);
        if (_datePreset == "all")
        {
            _dateFrom = _dateFrom == null || _dateFrom < LibraryDateMin || _dateFrom > LibraryDateMax ? LibraryDateMin : _dateFrom;
            _dateTo = _dateTo == null || _dateTo > LibraryDateMax || _dateTo < LibraryDateMin ? LibraryDateMax : _dateTo;
        }
        else ApplyDatePreset();
        OnPropertyChanged(nameof(LibraryDateMin)); OnPropertyChanged(nameof(LibraryDateMax));
        OnPropertyChanged(nameof(DateFrom)); OnPropertyChanged(nameof(DateTo));
    }

    public IReadOnlyList<SettingsChoice<string>> EventFilterOptions { get; } =
        new[] { new SettingsChoice<string>("all", "全部事件"), new("full_game", "整局录像") }
            .Concat(RecordingEventCatalog.All.Select(e => new SettingsChoice<string>(e.Id, e.Label))).ToArray();
    public IReadOnlyList<SettingsChoice<string>> SortOptions { get; } =
        [new("newest", "日期：从新到旧"), new("oldest", "日期：从旧到新"), new("event", "事件：多杀优先")];
    public IReadOnlyList<SettingsChoice<string>> DatePresets { get; } =
        [new("all", "全部日期"), new("today", "今天"), new("week", "最近 7 天")];
    public string DatePreset
    {
        get => _datePreset;
        set
        {
            if (value is not ("all" or "today" or "week") || _datePreset == value) return;
            _datePreset = value;
            ApplyDatePreset();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateFrom));
            OnPropertyChanged(nameof(DateTo));
            ApplyFilter(SelectedVideo?.Path);
        }
    }

    private void ApplyDatePreset()
    {
        var today = DateTime.Today;
        _dateFrom = _datePreset switch
        {
            "today" => new DateTimeOffset(today),
            "week" => new DateTimeOffset(today.AddDays(-6)),
            _ => LibraryDateMin
        };
        _dateTo = _datePreset == "all" ? LibraryDateMax : new DateTimeOffset(today);
    }
    public string EventFilter { get => _eventFilter; set { if (_eventFilter == value) return; _eventFilter = value; OnPropertyChanged(); ApplyFilter(SelectedVideo?.Path); } }
    public string SortOrder { get => _sort; set { if (_sort == value) return; _sort = value; OnPropertyChanged(); ApplyFilter(SelectedVideo?.Path); } }
    public DateTimeOffset? DateFrom { get => _dateFrom; set { _dateFrom = value; OnPropertyChanged(); ApplyFilter(SelectedVideo?.Path); } }
    public DateTimeOffset? DateTo { get => _dateTo; set { _dateTo = value; OnPropertyChanged(); ApplyFilter(SelectedVideo?.Path); } }
    public ObservableRangeCollection<LibraryVideoCard> Cards { get; } = new();
    public LibraryVideoCard? SelectedCard { get => Cards.FirstOrDefault(c => c.Video == SelectedVideo); set { if (value != null) SelectedVideo = value.Video; } }
    public bool MultiSelect
    {
        get => _multiSelect;
        set
        {
            if (_multiSelect == value) return;
            _multiSelect = value;
            foreach (var card in Cards) card.ShowSelection = value;
            if (!value) MarkVisible(false);
            OnPropertyChanged();
        }
    }
    public int MarkedCount => Cards.Count(card => card.IsMarked);
    public bool HasMarkedVideos => MarkedCount > 0;
    public string SelectionText => $"已选 {MarkedCount} 个";
    public void MarkVisible(bool marked)
    {
        _updatingMarks = true;
        try
        {
            foreach (var card in Cards) card.IsMarked = marked;
        }
        finally { _updatingMarks = false; }
        NotifySelection();
    }
    public void ResetFilters()
    {
        _eventFilter = _datePreset = "all";
        _sort = "newest";
        _searchQuery = "";
        ApplyDatePreset();
        foreach (var property in new[] { nameof(EventFilter), nameof(SortOrder), nameof(DatePreset), nameof(DateFrom), nameof(DateTo), nameof(SearchQuery) })
            OnPropertyChanged(property);
        ApplyFilter(SelectedVideo?.Path);
    }

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
        _quota = new(paths);
        _titles = LoadTitles();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PlayCommand = new RelayCommand(PlaySelected);
        RevealCommand = new RelayCommand(RevealSelected);
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
        : Videos.Count == _allVideos.Count
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
        if (_disposed) return;
        IsRefreshing = true;
        var version = ++_scanVersion;
        OnPropertyChanged(nameof(EmptyText));
        var directory = _paths.ResolveOutputDirectory(_config.OutputDir);
        try
        {
            while (true)
            {
                var fileVersion = _fileVersion;
                var (videos, indexed) = await Task.Run(() =>
                {
                    var items = _videoScanner.ScanVideos(directory);
                    var events = items.ToDictionary(v => v.Path, v => VideoTags.ReadIds(_metadataFooterService, v.Path), StringComparer.OrdinalIgnoreCase);
                    return (items, events);
                });
                if (_disposed || version != _scanVersion || directory != _paths.ResolveOutputDirectory(_config.OutputDir)) return;
                if (fileVersion != _fileVersion) continue;
                var selectedPath = SelectedVideo?.Path;
                _allVideos.Clear();
                _allVideos.AddRange(videos);
                _events.Clear();
                foreach (var entry in indexed) _events[entry.Key] = entry.Value;
                UpdateDateRange();
                ApplyFilter(selectedPath);
                await _detailsTask;
                break;
            }
        }
        catch (Exception error) { if (!_disposed && version == _scanVersion) ShowStatus($"无法读取录像目录：{error.Message}", true); }
        finally
        {
            if (version == _scanVersion)
            {
                IsRefreshing = false;
                OnPropertyChanged(nameof(EmptyText));
            }
        }
    }

    public void ReportPlaybackError(string message) => ShowStatus($"预览失败：{message}", true);
    public void ReportGradingBusy() => ShowStatus("已有调色任务，请等待任务完成或先取消。", true);

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

    internal bool DeleteToRecycleBin => _config.DeleteToRecycleBin;

    internal HashSet<string> DeleteVideos(IEnumerable<VideoItem> videos, bool recycle)
    {
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var deleted = 0;
        foreach (var video in videos.DistinctBy(video => video.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (recycle) RecycleBin.MoveFile(video.Path);
                else File.Delete(video.Path);
                removed.Add(video.Path);
                deleted++;
            }
            catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException) { removed.Add(video.Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                failures.Add($"{Path.GetFileName(video.Path)}：{e.Message}");
            }
        }
        if (removed.Count > 0)
        {
            _fileVersion++;
            _allVideos.RemoveAll(item => removed.Contains(item.Path));
            foreach (var path in removed) _events.Remove(path);
            var titles = new Dictionary<string, string>(_titles, StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var path in removed) changed |= titles.Remove(path);
            if (changed)
            {
                try { SaveTitles(titles); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures.Add($"无法清理已删除录像的库内标题：{e.Message}"); }
            }
            UpdateDateRange();
            ApplyFilter(SelectedVideo?.Path);
        }
        var message = recycle ? $"已将 {deleted} 个录像移入回收站" : $"已永久删除 {deleted} 个录像";
        if (removed.Count > deleted) message += $" · {removed.Count - deleted} 个文件已不存在";
        if (failures.Count > 0) message += $" · {failures.Count} 项失败 · {failures[0]}";
        ShowStatus(message, failures.Count > 0);
        return removed;
    }

    public void RenameTitle(LibraryVideoCard card, string title)
    {
        var titles = new Dictionary<string, string>(_titles, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(title)) titles.Remove(card.Path);
        else titles[card.Path] = title.Trim();
        try
        {
            SaveTitles(titles);
            ApplyFilter(SelectedVideo?.Path);
            ShowStatus("已更新库内标题", false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"无法修改库内标题：{e.Message}", true);
        }
    }

    public string? RenameFile(LibraryVideoCard card, string newName)
    {
        var name = newName.Trim();
        if (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' ') || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus("请输入有效的录像文件名。", true);
            return null;
        }
        var destination = Path.Combine(Path.GetDirectoryName(card.Path)!, name + Path.GetExtension(card.Path));
        if (destination == card.Path) return destination;
        if (!string.Equals(card.Path, destination, StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
        {
            ShowStatus("已有同名录像。", true);
            return null;
        }

        var renamed = card.Video with { Path = destination, Name = name };
        var originalTitles = _titles;
        try
        {
            // Keep both mappings until the filesystem rename succeeds.
            if (card.Title != renamed.DisplayName || _titles.ContainsKey(card.Path))
            {
                var staged = new Dictionary<string, string>(_titles, StringComparer.OrdinalIgnoreCase) { [destination] = card.Title };
                SaveTitles(staged);
            }
            _quota.MoveVideo(card.Path, destination);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            if (!ReferenceEquals(_titles, originalTitles))
            {
                try { SaveTitles(originalTitles); }
                catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException) { }
            }
            ShowStatus($"无法修改文件名：{e.Message}", true);
            return null;
        }

        _fileVersion++;
        var index = _allVideos.FindIndex(video => string.Equals(video.Path, card.Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _allVideos[index] = renamed;
        if (_events.Remove(card.Path, out var ids)) _events[destination] = ids;
        var titles = new Dictionary<string, string>(_titles, StringComparer.OrdinalIgnoreCase);
        string? cleanupError = null;
        if (!string.Equals(card.Path, destination, StringComparison.OrdinalIgnoreCase) && titles.Remove(card.Path))
        {
            try { SaveTitles(titles); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { cleanupError = e.Message; }
        }
        ApplyFilter(destination);
        ShowStatus(cleanupError == null ? "已修改文件名" : $"已修改文件名，旧标题索引清理失败：{cleanupError}", cleanupError != null);
        return destination;
    }

    private string TitlesPath => Path.Combine(_paths.DataRoot, "library-titles.json");
    private Dictionary<string, string> LoadTitles()
    {
        try
        {
            if (File.Exists(TitlesPath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TitlesPath)) is { } titles)
                return new(titles, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveTitles(Dictionary<string, string> titles)
    {
        Directory.CreateDirectory(_paths.DataRoot);
        var temp = TitlesPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(titles));
        File.Move(temp, TitlesPath, true);
        _titles = titles;
    }

    private void ApplyFilter(string? selectedPath)
    {
        var query = SearchQuery.Trim();
        var filtered = _allVideos
            .Where(video => query.Length == 0 ||
                            Path.GetFileName(video.Path).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            TitleFor(video).Contains(query, StringComparison.OrdinalIgnoreCase) ||
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
        var marked = Cards.Where(card => card.IsMarked).Select(card => card.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var card in Cards) card.PropertyChanged -= CardChanged;
        var cards = Videos.Select(CreateCard).ToArray();
        foreach (var card in cards)
        {
            card.IsMarked = marked.Contains(card.Path);
            card.PropertyChanged += CardChanged;
        }
        Cards.ReplaceAll(cards);
        NotifySelection();

        HasVideos = Videos.Count > 0;
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyText));

        var keep = selectedPath == null
            ? null
            : Videos.FirstOrDefault(video => string.Equals(video.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        SelectedVideo = keep ?? Videos.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCard));
    }

    public LibraryVideoCard? FindCard(string path)
    {
        return Cards.FirstOrDefault(card => string.Equals(card.Path, path, StringComparison.OrdinalIgnoreCase)) ??
            (_allVideos.FirstOrDefault(video => string.Equals(video.Path, path, StringComparison.OrdinalIgnoreCase)) is { } video ? CreateCard(video) : null);
    }

    private string TitleFor(VideoItem video) => _titles.GetValueOrDefault(video.Path) ?? video.DisplayName;
    private LibraryVideoCard CreateCard(VideoItem video) => new(video, EventLabels(video.Path), TitleFor(video)) { ShowSelection = MultiSelect };
    private void CardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingMarks && e.PropertyName == nameof(LibraryVideoCard.IsMarked)) NotifySelection();
    }
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasMarkedVideos));
    }

    private string[] EventLabels(string path) => _events.TryGetValue(path, out var ids) && ids.Length > 0
        ? ids.Select(VideoTags.Label).ToArray() : ["无事件标签"];
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

public sealed class LibraryVideoCard(VideoItem video, string[] tags, string? title = null) : GradingObservable
{
    private bool _isMarked, _showSelection;
    public VideoItem Video { get; } = video;
    public string[] Tags { get; } = tags;
    public string Path => Video.Path;
    public string Title { get; } = title ?? video.DisplayName;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Meta => $"{Video.MetaText} · {Video.SizeText}";
    public string TagText => string.Join("  ·  ", Tags.Take(4));
    public bool IsMarked { get => _isMarked; set => Set(ref _isMarked, value); }
    public bool ShowSelection { get => _showSelection; set => Set(ref _showSelection, value); }
}
