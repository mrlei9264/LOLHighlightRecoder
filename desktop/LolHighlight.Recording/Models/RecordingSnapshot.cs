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
    PlayerIdentity? Player);

public sealed class RecordingStatusChangedEventArgs : EventArgs
{
    public RecordingSnapshot Snapshot { get; }
    public RecordingStatusChangedEventArgs(RecordingSnapshot snapshot) => Snapshot = snapshot;
}
