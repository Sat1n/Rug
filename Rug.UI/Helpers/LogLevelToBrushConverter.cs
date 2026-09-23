using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

using Rug.UI.Models;

namespace Rug.UI.Helpers;

/// <summary>
/// Maps a <see cref="LogLevel"/> to a console foreground brush. The log panel uses a
/// fixed dark background, so these bright colors read well in both light and dark theme.
/// </summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Info = new(Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4));
    private static readonly SolidColorBrush Success = new(Color.FromArgb(0xFF, 0x4E, 0xC9, 0x4E));
    private static readonly SolidColorBrush Warn = new(Color.FromArgb(0xFF, 0xE2, 0xC0, 0x8D));
    private static readonly SolidColorBrush Error = new(Color.FromArgb(0xFF, 0xF1, 0x4C, 0x4C));

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is LogLevel level ? level switch
        {
            LogLevel.Success => Success,
            LogLevel.Warn => Warn,
            LogLevel.Error => Error,
            _ => Info,
        } : Info;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
