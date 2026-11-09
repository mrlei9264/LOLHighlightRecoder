namespace LolHighlight.Recording.Models;

public enum RecordingStatus
{
    Idle,
    Starting,
    WaitingForGame,
    Recording,
    Exporting,
    Stopping,
    Error,
    Paused,
    Retrying
}

public sealed record RecordingSnapshot(
    RecordingStatus Status,
    string Title,
    string Detail,
    double BufferedSeconds,
    string? LastError,
    PlayerIdentity? Player)
{
    public bool IsCapturingVideo { get; init; }
    public DateTime? CaptureStartedAtUtc { get; init; }
    public bool IsCapturingGameAudio { get; init; }
    public bool IsCapturingMicrophone { get; init; }
}

public sealed class RecordingStatusChangedEventArgs : EventArgs
{
    public RecordingSnapshot Snapshot { get; }
    public RecordingStatusChangedEventArgs(RecordingSnapshot snapshot) => Snapshot = snapshot;
}
