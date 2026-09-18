using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FolderDiff.Infrastructure;

public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name)
        {
            var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (type.IsEnum)
                return Enum.Parse(type, name);
        }

        return Binding.DoNothing;
    }
}

public sealed class AutoNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int number && number == 0 ? "авто" : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public string TrueKey { get; set; } = "StatusAddedBrush";

    public string FalseKey { get; set; } = "StatusDeletedBrush";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is true ? TrueKey : FalseKey;
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class CheckMarkConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "✓" : "✗";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class PixelWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && width > 0
            ? new GridLength(width, GridUnitType.Pixel)
            : new GridLength(1, GridUnitType.Star);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is GridLength length ? length.Value : 0d;
}

public sealed class AutoWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && width > 0 ? width : double.NaN;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class EnumVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
