namespace LolHighlight.Recording.Models;

public sealed record ColorTone(string Id, string Name, string Description, string Swatch,
    double Contrast, double Saturation, double Gamma = 1, double RedShadows = 0, double BlueShadows = 0,
    double RedHighlights = 0, double BlueHighlights = 0, bool Monochrome = false)
{

}

public static class ColorTones
{
    public static IReadOnlyList<ColorTone> All { get; } = new ColorTone[]
    {
        new("natural", "自然通透", "轻微增强对比与色彩，接近原始画面。", "#A4BDAA", 1.04, 1.05, 1.01),
        new("cool", "冷青电竞", "青蓝阴影，突出明亮技能。", "#57BFCF", 1.08, 1.10, 1, -.03, .06, .015, .01),
        new("warm", "暖金时刻", "温暖高光与柔和金色氛围。", "#E9B36D", 1.06, 1.06, 1.02, .02, -.02, .07, -.04),
        new("cinema", "青橙电影", "冷色阴影与暖色高光分离。", "#5498B2", 1.13, .95, 1, -.04, .055, .065, -.045),
        new("vivid", "鲜明高饱和", "更强的色彩与明暗层次。", "#D48298", 1.10, 1.28, 1.02),
        new("soft", "柔和明亮", "提亮中间调，降低生硬的明暗反差。", "#C6C5B3", .92, .94, 1.08),
        new("ice", "冰蓝夜色", "偏蓝的冷调画面与克制的色彩。", "#75A9DE", 1.07, .92, 1.02, -.04, .09, -.01, .035),
        new("violet", "暮色紫调", "轻微紫色偏移，适合夜景与魔法技能。", "#AE91C9", 1.06, 1.04, 1.02, .04, .065, .025, .03),
        new("retro", "复古胶片", "低饱和暖调与柔和反差。", "#BD9F7C", .94, .76, 1.04, .045, -.025, .04, -.025),
        new("mono", "经典黑白", "去除色彩，保留清晰的明暗结构。", "#BBBBBB", 1.12, 1, 1, Monochrome: true)
    };
    public static ColorTone Get(string id) => All.FirstOrDefault(t => t.Id == id) ?? throw new ArgumentException("未知色调。");
}

public sealed record ColorGradeRequest(string SourcePath, string OutputPath, string ToneId, double Strength = .8);
public sealed record ColorGradeProgress(double Percent, string Message);
public sealed record ColorVideoInfo(double Duration, int Width, int Height, string[] AudioCodecs);
