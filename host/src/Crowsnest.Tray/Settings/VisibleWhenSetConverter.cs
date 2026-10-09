using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Crowsnest.Tray.Settings;

/// <summary>Visible for true, or for any other value that is there (not null, not an empty string); collapsed otherwise.</summary>
public sealed class VisibleWhenSetConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null or false or "" => Visibility.Collapsed,
        _ => Visibility.Visible,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
