using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FolderDiff.Models;

namespace FolderDiff.Infrastructure;

public sealed class DiffLineBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isRight = string.Equals(parameter as string, "Right", StringComparison.OrdinalIgnoreCase);

        var key = value switch
        {
            DiffLineKind.Added => "LineAddedBrush",
            DiffLineKind.Deleted => "LineDeletedBrush",
            DiffLineKind.Modified => isRight ? "LineAddedBrush" : "LineDeletedBrush",
            DiffLineKind.Filler => "LineFillerBrush",
            _ => null
        };

        if (key is null)
            return Brushes.Transparent;

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            FileStatus.Added => "StatusAddedBrush",
            FileStatus.Deleted => "StatusDeletedBrush",
            FileStatus.Modified => "StatusModifiedBrush",
            _ => "StatusUnchangedBrush"
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert)
            flag = !flag;

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
