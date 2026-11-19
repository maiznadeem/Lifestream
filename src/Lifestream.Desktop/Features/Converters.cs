using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Stock;
using Lifestream.Desktop.Controls;
using Lifestream.Domain;

namespace Lifestream.Desktop.Features;

/// <summary>A date shown in the one app format, "05 Mar 2022" (SHELL-36); a missing date shows the converter parameter or nothing.</summary>
public sealed class DateTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DateOnly date => Formatting.Date(date),
        _ => parameter as string ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A blood type shown as "A+".</summary>
public sealed class BloodTypeTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is BloodType type ? type.ToDisplay() : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Shows the first non-empty message of several (a field's rule message, or the date box's "use this format" message).</summary>
public sealed class FirstNonEmptyConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.OfType<string>().FirstOrDefault(s => s.Length > 0);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The stock tile's flag as a status chip: icon, text and token colour from the shared chip, never a bare colour.</summary>
public sealed class StockSeverityToStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StockSeverity.LowStock => StatusKind.LowStock,
        StockSeverity.ExpiringSoon => StatusKind.Expiring,
        _ => StatusKind.Completed,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Collapsed when the value is null, visible otherwise.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible only when the value is null (the opposite of <see cref="NullToCollapsedConverter"/>).</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
