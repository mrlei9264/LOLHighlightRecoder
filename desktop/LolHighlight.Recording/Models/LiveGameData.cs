namespace LolHighlight.Recording.Models;

public sealed record LiveGameData(Dictionary<string, object> Raw)
{
    public Dictionary<string, object> ActivePlayer => Raw.GetValueOrDefault("activePlayer") as Dictionary<string, object> ?? new();
    public Dictionary<string, object> Game => Raw.GetValueOrDefault("gameData") as Dictionary<string, object> ?? new();
    public List<Dictionary<string, object>> Players =>
        (Raw.GetValueOrDefault("allPlayers") as IEnumerable<object>)?.OfType<Dictionary<string, object>>().ToList() ?? new();
    public List<Dictionary<string, object>> Events =>
        ((Raw.GetValueOrDefault("events") as Dictionary<string, object>)?.GetValueOrDefault("Events") as IEnumerable<object>)?
        .OfType<Dictionary<string, object>>().ToList() ?? new();
}
