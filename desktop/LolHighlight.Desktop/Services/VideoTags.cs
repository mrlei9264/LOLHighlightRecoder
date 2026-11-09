namespace LolHighlight.Desktop.Services;

internal static class VideoTags
{
    public static string[] ReadIds(IMetadataFooterService footer, string path)
    {
        try
        {
            var metadata = footer.ReadMetadataFooter(path);
            var ids = (metadata?.GetValueOrDefault("triggers") as IEnumerable<object> ?? [])
                .OfType<Dictionary<string, object>>()
                .Select(t => t.GetValueOrDefault("kind")?.ToString() ?? "")
                .Where(k => k.Length > 0);
            if (metadata?.GetValueOrDefault("type")?.ToString() == "full_game") ids = ids.Prepend("full_game");
            return ids.Distinct().ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return []; }
    }

    public static string Label(string id) => id == "full_game" ? "整局录像" : RecordingEventCatalog.All.FirstOrDefault(e => e.Id == id)?.Label ?? id;
    public static string Format(string[] ids) => ids.Length == 0 ? "无事件" : string.Join(" · ", ids.Select(Label));
    public static string Read(IMetadataFooterService footer, string path) => Format(ReadIds(footer, path));
}
