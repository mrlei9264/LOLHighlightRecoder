namespace LolHighlight.Recording.Models;

public sealed class ClipWindow
{
    public double StartedAt { get; set; }
    public double EndsAt { get; set; }
    public PlayerIdentity Player { get; set; } = null!;
    public List<HighlightTrigger> Triggers { get; set; } = new();
    public bool IsContinuation { get; set; }

    public ClipWindow Snapshot() => new()
    {
        StartedAt = StartedAt,
        EndsAt = EndsAt,
        Player = Player,
        Triggers = Triggers.ToList(),
        IsContinuation = IsContinuation
    };
}
