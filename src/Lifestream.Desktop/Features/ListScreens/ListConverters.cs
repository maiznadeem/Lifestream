using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Lifestream.Client.Features.Donors;
using Lifestream.Desktop.Controls;
using Lifestream.Domain;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>A blood group with a true minus sign ("O−"), for the blood group tags of the list screens.</summary>
public sealed class TypographicBloodTypeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is BloodType type ? type.ToTypographic() : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"AM" for "Adrian Mukherjee", for a person's avatar.</summary>
public sealed class InitialsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => DonorsViewModel.PersonInitials(value as string ?? string.Empty);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A record id as its code: the parameter is the prefix ("D" gives "D-0175").</summary>
public sealed class RecordCodeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int id ? $"{parameter as string ?? "#"}-{id.ToString("0000", CultureInfo.InvariantCulture)}" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A donor row and today (a multi-binding) as a part of the donor's standing (<see cref="DonorStanding"/>: eligible, first-time, waiting until a date, on hold), chosen by
/// the parameter: "Text" (the badge's word), "Kind" (its colour) or "Dot" (true when eligible).
/// </summary>
public sealed class DonorStandingConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [Lifestream.Contracts.Donors.DonorListItem donor, DateOnly today, ..])
        {
            return parameter as string == "Dot" ? false : parameter as string == "Kind" ? BadgeKind.Neutral : string.Empty;
        }

        var standing = DonorStanding.Of(donor.DateOfBirth, donor.LastDonated, donor.MedicalHold, today);
        return (parameter as string) switch
        {
            "Kind" => standing.Kind switch
            {
                DonorStandingKind.Eligible => BadgeKind.Success,
                DonorStandingKind.FirstTime => BadgeKind.Info,
                DonorStandingKind.OnHold => BadgeKind.Warning,
                _ => BadgeKind.Neutral,
            },
            "Dot" => standing.Kind == DonorStandingKind.Eligible,
            _ => standing.Text,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound value is one of the parameter's names ("Critical|Soon", enum values by name), collapsed otherwise (and for null).</summary>
public sealed class EqualsToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() is { } text && (parameter as string ?? string.Empty).Split('|').Contains(text, StringComparer.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The side panel's width for a breakpoint: Width.PanelCompact (352) in a compact window, Width.Panel (420) otherwise.</summary>
public sealed class PanelWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Lifestream.Desktop.Theme.ThemeTokens.Space(value is Breakpoint.Compact ? "Width.PanelCompact" : "Width.Panel");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True in a compact window (below 1280 DIP): buttons fold to their icons.</summary>
public sealed class IsCompactConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is Breakpoint.Compact;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
