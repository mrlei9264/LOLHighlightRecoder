namespace LolHighlight.Recording.Models;

public sealed record RecordingEventKind(string Id, string Label, bool DefaultEnabled = true);

public static class RecordingEventCatalog
{
    public static IReadOnlyList<RecordingEventKind> All { get; } = new[]
    {
        new RecordingEventKind("kill", "击杀"),
        new RecordingEventKind("multikill_2", "双杀"),
        new RecordingEventKind("multikill_3", "三杀"),
        new RecordingEventKind("multikill_4", "四杀"),
        new RecordingEventKind("multikill_5", "五杀"),
        new RecordingEventKind("first_blood", "一血"),
        new RecordingEventKind("ace", "己方团灭对手"),
        new RecordingEventKind("dragonkill", "参与击杀 / 抢夺小龙"),
        new RecordingEventKind("heraldkill", "参与击杀 / 抢夺先锋"),
        new RecordingEventKind("baronkill", "参与击杀 / 抢夺大龙"),
        new RecordingEventKind("turretkilled", "参与摧毁防御塔"),
        new RecordingEventKind("inhibkilled", "参与摧毁水晶"),
        new RecordingEventKind("assist", "助攻", false),
        new RecordingEventKind("death", "阵亡", false)
    };

    public static string[] Defaults() => All.Where(kind => kind.DefaultEnabled).Select(kind => kind.Id).ToArray();
}

public sealed class HighlightDetectedEventArgs(HighlightTrigger trigger, bool fullGame) : EventArgs
{
    public HighlightTrigger Trigger { get; } = trigger;
    public bool FullGame { get; } = fullGame;
}
