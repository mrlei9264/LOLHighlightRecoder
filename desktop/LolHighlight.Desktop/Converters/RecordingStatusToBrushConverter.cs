using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Converters;

public sealed class RecordingStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var status = value is RecordingStatus recordingStatus ? recordingStatus : RecordingStatus.Idle;

        var color = status switch
        {
            RecordingStatus.Recording => Color.FromArgb(255, 198, 242, 91),
            RecordingStatus.WaitingForGame or RecordingStatus.Paused or RecordingStatus.Retrying => Color.FromArgb(255, 242, 201, 76),
            RecordingStatus.Starting => Color.FromArgb(255, 242, 201, 76),
            RecordingStatus.Exporting => Color.FromArgb(255, 242, 201, 76),
            RecordingStatus.Stopping => Color.FromArgb(255, 242, 201, 76),
            RecordingStatus.Error => Color.FromArgb(255, 240, 113, 120),
            _ => Color.FromArgb(255, 122, 132, 142)
        };

        if (string.Equals(parameter as string, "soft", StringComparison.OrdinalIgnoreCase))
            color = Color.FromArgb(34, color.R, color.G, color.B);

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
