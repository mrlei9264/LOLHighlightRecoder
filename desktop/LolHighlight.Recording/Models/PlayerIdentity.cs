namespace LolHighlight.Recording.Models;

public sealed record PlayerIdentity(
    string RiotId,
    string GameName,
    string TagLine = "",
    string Team = "")
{
    public bool Matches(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (candidate.Contains('#'))
        {
            var qualifiedId = RiotId.Contains('#') ? RiotId :
                string.IsNullOrWhiteSpace(TagLine) ? null : $"{GameName}#{TagLine}";
            if (qualifiedId != null)
                return string.Equals(candidate, qualifiedId, StringComparison.OrdinalIgnoreCase);
            candidate = candidate.Split('#')[0];
        }
        return string.Equals(candidate, GameName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(candidate, RiotId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(candidate, RiotId.Split('#')[0], StringComparison.OrdinalIgnoreCase);
    }
}
