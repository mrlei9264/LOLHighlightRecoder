namespace LolHighlight.Recording.Models;

public sealed record HighlightTrigger(
    int EventId,
    string Kind,
    string Label,
    double EventTime,
    DateTime DetectedAt,
    Dictionary<string, object> RawEvent)
{
    public double? RecordingSeconds { get; init; }
}
