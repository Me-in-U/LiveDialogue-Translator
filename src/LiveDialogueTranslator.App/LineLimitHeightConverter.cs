using System.Globalization;
using System.Windows.Data;

namespace LiveDialogueTranslator.App;

public sealed class FontSizeToLineHeightConverter : IValueConverter
{
    private const double LineHeightMultiplier = 1.35;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is double fontSize ? fontSize * LineHeightMultiplier : 18.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class LineLimitHeightConverter : IMultiValueConverter
{
    private const double LineHeightMultiplier = 1.35;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var lines = values.Length > 0 && values[0] is int lineCount ? Math.Max(1, lineCount) : 1;
        var fontSize = values.Length > 1 && values[1] is double size ? size : 13.0;
        return lines * fontSize * LineHeightMultiplier;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
