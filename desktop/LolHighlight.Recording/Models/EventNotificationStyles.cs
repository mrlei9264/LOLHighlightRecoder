namespace LolHighlight.Recording.Models;

public sealed record EventNotificationStyleChoice(string Id, string Label, string Description);

public static class EventNotificationStyles
{
    public const string Default = "pulse";

    public static IReadOnlyList<EventNotificationStyleChoice> All { get; } = Array.AsReadOnly(new[]
    {
        new EventNotificationStyleChoice("neon", "霓虹超载", "青紫霓虹切角，高速滑入，电流扫光后向右退出。"),
        new EventNotificationStyleChoice("ink", "赤墨斩击", "黑红水墨笔触，斜向劈入，墨迹消散退场。"),
        new EventNotificationStyleChoice("crystal", "棱晶裂隙", "紫色不对称晶面，碎片聚合，折射散开退场。"),
        new EventNotificationStyleChoice("blade", "疾风刀锋", "银蓝纤薄横幅，速度线滑入，沿刀锋划出。"),
        new EventNotificationStyleChoice("pulse", "灵动脉冲", "紧凑绿色胶囊，弹性展开，脉冲扩散后收回光点。"),
        new EventNotificationStyleChoice("comic", "漫画暴击", "亮黄漫画网点，撞击弹跳，爆炸气泡缩走。"),
        new EventNotificationStyleChoice("forge", "熔炉锻造", "橙色熔炉装甲，重击落位，火星喷溅后沉降退出。"),
        new EventNotificationStyleChoice("aurora", "极光织带", "柔光交叠织带，错层漂入，流光扫过后丝带抽离。")
    });

    public static bool IsSupported(string? id) => All.Any(style => style.Id == id);
}
