using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace LolHighlight.Desktop.Converters;

public sealed class BoolToInfoBarSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
