using System.Globalization;
using System.Text;
using System.Text.Json;
using LolHighlight.Recording.Infrastructure;

namespace LolHighlight.Recording.Services;

public sealed record TelemetryWindow(List<Dictionary<string, object>> Records, bool Truncated);

public static class TelemetryReader
{
    public static TelemetryWindow ReadTelemetryWindow(string path, DateTime startedAt, DateTime endsAt,
        CancellationToken cancellationToken = default)
    {
        var records = new List<Dictionary<string, object>>();
        var bytes = 0;
        foreach (var file in new[] { path + ".previous", path })
        {
            if (!File.Exists(file)) continue;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var session = root.TryGetProperty("type", out var type) && type.GetString() == "session";
                    if (!session && (!root.TryGetProperty("captured_at", out var captured) ||
                        !DateTime.TryParse(captured.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var timestamp) ||
                        timestamp < startedAt || timestamp > endsAt)) continue;
                    bytes += Encoding.UTF8.GetByteCount(line);
                    if (bytes > 8 * 1024 * 1024 || records.Count >= 4000)
                        return new TelemetryWindow(records, true);
                    if (JsonValues.Normalize(root) is Dictionary<string, object> record)
                        records.Add(record);
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
                { /* Ignore incomplete or malformed records. */ }
            }
        }
        return new TelemetryWindow(records, false);
    }
}
