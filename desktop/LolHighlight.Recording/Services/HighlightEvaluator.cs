using LolHighlight.Recording.Models;

namespace LolHighlight.Recording.Services;

public interface IHighlightEvaluator
{
    HighlightTrigger? Evaluate(Dictionary<string, object> evt, PlayerIdentity player, DateTime? detectedAt = null);
}

public sealed class HighlightEvaluator : IHighlightEvaluator
{
    private static readonly Dictionary<string, string> ObjectiveLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DragonKill"] = "小龙",
        ["HeraldKill"] = "峡谷先锋",
        ["BaronKill"] = "大龙",
        ["TurretKilled"] = "防御塔",
        ["InhibKilled"] = "水晶"
    };

    private static readonly Dictionary<int, string> MultikillLabels = new()
    {
        [2] = "双杀",
        [3] = "三杀",
        [4] = "四杀",
        [5] = "五杀"
    };

    public HighlightTrigger? Evaluate(Dictionary<string, object> evt, PlayerIdentity player, DateTime? detectedAt = null)
    {
        var eventName = evt.GetValueOrDefault("EventName")?.ToString() ?? "";
        var eventId = GetInt(evt.GetValueOrDefault("EventID"), -1);
        var eventTime = GetDouble(evt.GetValueOrDefault("EventTime"), 0.0);
        var detectedAtValue = detectedAt ?? DateTime.UtcNow;

        string? kind = null;
        string? label = null;

        if (eventName.Equals("ChampionKill", StringComparison.OrdinalIgnoreCase) && IsKiller(evt, player))
        {
            kind = "kill";
            label = $"击杀 {evt.GetValueOrDefault("VictimName")}";
        }
        else if (eventName.Equals("ChampionKill", StringComparison.OrdinalIgnoreCase) && SamePlayer(evt.GetValueOrDefault("VictimName"), player))
        {
            kind = "death";
            label = "阵亡";
        }
        else if (eventName.Equals("ChampionKill", StringComparison.OrdinalIgnoreCase) && IsInvolved(evt, player))
        {
            kind = "assist";
            label = $"助攻击杀 {evt.GetValueOrDefault("VictimName")}";
        }
        else if (eventName.Equals("Multikill", StringComparison.OrdinalIgnoreCase) && IsKiller(evt, player))
        {
            var streak = GetInt(evt.GetValueOrDefault("KillStreak"), 0);
            kind = $"multikill_{streak}";
            label = MultikillLabels.TryGetValue(streak, out var lbl) ? lbl : $"连续击杀 {streak}";
        }
        else if (eventName.Equals("FirstBlood", StringComparison.OrdinalIgnoreCase) &&
                 (IsRecipient(evt, player) || IsKiller(evt, player)))
        {
            kind = "first_blood";
            label = "第一滴血";
        }
        else if (eventName.Equals("Ace", StringComparison.OrdinalIgnoreCase) && IsAcer(evt, player))
        {
            kind = "ace";
            label = "团灭";
        }
        else if (ObjectiveLabels.TryGetValue(eventName, out var objectiveLabel))
        {
            if (IsKiller(evt, player) || IsInvolved(evt, player))
            {
                var stolen = IsTrue(evt.GetValueOrDefault("Stolen"));
                kind = eventName.ToLowerInvariant();
                label = stolen ? $"抢下{objectiveLabel}" : $"参与{objectiveLabel}";
            }
        }

        if (kind == null || label == null)
            return null;

        return new HighlightTrigger(
            EventId: eventId,
            Kind: kind,
            Label: label,
            EventTime: eventTime,
            DetectedAt: detectedAtValue,
            RawEvent: evt);
    }

    private static bool IsKiller(Dictionary<string, object> evt, PlayerIdentity player)
        => SamePlayer(evt.GetValueOrDefault("KillerName"), player);

    private static bool IsRecipient(Dictionary<string, object> evt, PlayerIdentity player)
        => SamePlayer(evt.GetValueOrDefault("Recipient"), player);

    private static bool IsAcer(Dictionary<string, object> evt, PlayerIdentity player)
    {
        var acer = evt.GetValueOrDefault("Acer")?.ToString();
        var acingTeam = evt.GetValueOrDefault("AcingTeam")?.ToString();

        if (!string.IsNullOrWhiteSpace(player.Team))
        {
            if (string.Equals(acingTeam, player.Team, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(acer, player.Team, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return SamePlayer(acer, player);
    }

    private static bool IsInvolved(Dictionary<string, object> evt, PlayerIdentity player)
    {
        if (evt.TryGetValue("Assisters", out var assistersObj) && assistersObj is IEnumerable<object> assisters)
        {
            foreach (var assist in assisters)
            {
                if (SamePlayer(assist, player))
                    return true;
            }
        }
        return false;
    }

    private static int GetInt(object? value, int fallback)
    {
        if (value is int intValue) return intValue;
        if (value is long longValue && longValue <= int.MaxValue && longValue >= int.MinValue) return (int)longValue;
        return int.TryParse(value?.ToString(), out var parsed) ? parsed : fallback;
    }

    private static double GetDouble(object? value, double fallback)
    {
        if (value is double doubleValue) return doubleValue;
        if (value is float floatValue) return floatValue;
        if (value is int intValue) return intValue;
        if (value is long longValue) return longValue;
        return double.TryParse(value?.ToString(), out var parsed) ? parsed : fallback;
    }

    private static bool IsTrue(object? value)
    {
        return value is bool boolValue
            ? boolValue
            : bool.TryParse(value?.ToString(), out var parsed) && parsed;
    }

    private static bool SamePlayer(object? value, PlayerIdentity player)
        => player.Matches(value?.ToString());
}
