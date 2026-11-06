using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.ViewModels;

internal static class RecordingStatusExtensions
{
    public static bool IsActive(this RecordingStatus status)
        => status is not RecordingStatus.Idle and not RecordingStatus.Error;

    public static string ToLabel(this RecordingStatus status) => status switch
    {
        RecordingStatus.Starting => "启动中",
        RecordingStatus.WaitingForGame => "等待对局",
        RecordingStatus.Paused => "已暂停",
        RecordingStatus.Retrying => "采集重试",
        RecordingStatus.Recording => "录制中",
        RecordingStatus.Exporting => "保存高光",
        RecordingStatus.Stopping => "停止中",
        RecordingStatus.Error => "异常",
        _ => "已停止"
    };

    public static string ToGlyph(this RecordingStatus status) => status switch
    {
        RecordingStatus.Recording => "\uE7C8",
        RecordingStatus.WaitingForGame => "\uE823",
        RecordingStatus.Paused => "\uE769",
        RecordingStatus.Retrying => "\uE72C",
        RecordingStatus.Starting => "\uE895",
        RecordingStatus.Exporting => "\uE74E",
        RecordingStatus.Stopping => "\uE71A",
        RecordingStatus.Error => "\uE783",
        _ => "\uE71A"
    };
}
