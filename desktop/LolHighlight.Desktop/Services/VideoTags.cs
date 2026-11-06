namespace LolHighlight.Desktop.Services;

internal static class VideoTags
{
    public static string Read(IMetadataFooterService footer, string path)
    {
        try
        {
            var metadata = footer.ReadMetadataFooter(path);
            if (metadata?.GetValueOrDefault("type")?.ToString() == "full_game") return "整局录像";
            var tags = (metadata?.GetValueOrDefault("triggers") as IEnumerable<object> ?? [])
                .OfType<Dictionary<string, object>>()
                .Select(t => t.GetValueOrDefault("kind")?.ToString() ?? "")
                .Where(k => k.Length > 0).Distinct()
                .Select(k => RecordingEventCatalog.All.FirstOrDefault(e => e.Id == k)?.Label ?? k);
            var text = string.Join(" · ", tags);
            return text.Length == 0 ? "无事件" : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return "无事件"; }
    }
}
