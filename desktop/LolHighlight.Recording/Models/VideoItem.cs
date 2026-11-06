namespace LolHighlight.Recording.Models;

public sealed record VideoItem(
    string Path,
    string Name,
    string Summary,
    DateTime CreatedAt = default,
    long SizeBytes = 0)
{
    public string DisplayName => CreatedAt == default
        ? Name
        : $"{CreatedAt:MM-dd HH:mm} 的{(Name.StartsWith("fullgame_", StringComparison.OrdinalIgnoreCase) ? "整局录像" : "高光")}";

    public string MetaText => CreatedAt == default
        ? Summary
        : $"{CreatedAt:yyyy-MM-dd HH:mm}";

    public string SizeText => SizeBytes switch
    {
        <= 0 => "未知大小",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024):0.0} MB",
        _ => $"{SizeBytes / (1024.0 * 1024 * 1024):0.00} GB"
    };

    public override string ToString() => $"{Name}\n{Summary}";
}
