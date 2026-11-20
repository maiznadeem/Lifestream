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
        value is int id && parameter is string prefix ? Lifestream.Client.Features.Receipts.RecordCodes.Of(prefix, id) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A donor row and today (a multi-binding) as a part of the donor's standing (<see cref="DonorStanding"/>: eligible, first-time, waiting until a date, on hold, not
/// eligible), chosen by the parameter: "Text" (the badge's word), "Kind" (its colour), "Dot" (true when eligible) or "Icon" (a clock for a wait, an alert for a hold or
/// "Not eligible", an info mark for a first-time donor). Every pill carries a dot or an icon with its word (design system 1).
/// </summary>
public sealed class DonorStandingConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [Lifestream.Contracts.Donors.DonorListItem donor, DateOnly today, ..])
        {
            return (parameter as string) switch
            {
                "Dot" => false,
                "Kind" => BadgeKind.Neutral,
                "Icon" => IconKind.None,
                _ => string.Empty,
            };
        }

        var standing = DonorStanding.Of(donor.DateOfBirth, donor.LastDonated, donor.MedicalHold, today);
        return (parameter as string) switch
        {
            "Kind" => standing.Kind switch
            {
                DonorStandingKind.Eligible => BadgeKind.Success,
                DonorStandingKind.FirstTime => BadgeKind.Info,
                DonorStandingKind.OnHold => BadgeKind.Warning,
                DonorStandingKind.NotEligible => BadgeKind.Critical,
                _ => BadgeKind.Neutral,
            },
            "Dot" => standing.Kind == DonorStandingKind.Eligible,
            "Icon" => standing.Kind switch
            {
                DonorStandingKind.Waiting => IconKind.Clock,
                DonorStandingKind.FirstTime => IconKind.Info,
                DonorStandingKind.OnHold or DonorStandingKind.NotEligible => IconKind.Alert,
                _ => IconKind.None,
            },
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

/// <summary>Visible when the bound value is not null and not an empty string or list (a section with something in it).</summary>
public sealed class HasContentToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        null => Visibility.Collapsed,
        string s => s.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
        System.Collections.ICollection c => c.Count == 0 ? Visibility.Collapsed : Visibility.Visible,
        _ => Visibility.Visible,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A UTC instant as its local date, "16 Nov 2026" (the user's own zone).</summary>
public sealed class UtcDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime utc ? Lifestream.Client.Common.Formatting.Date(Lifestream.Client.Features.ListScreens.RecordFormatting.LocalDate(utc)) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound value is false, collapsed when it is true.</summary>
public sealed class FalseToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A person's age today from their date of birth and today (a multi-binding), "42".</summary>
public sealed class AgeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [DateOnly birth, DateOnly today, ..] ? Eligibility.AgeOn(birth, today).ToString(CultureInfo.CurrentCulture) : string.Empty;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A count for a tab or a header ("12"), or nothing while it is not known (never a made-up 0).</summary>
public sealed class CountTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int n ? n.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Collapsed in a compact window (below 1280 DIP), visible otherwise: a button's label that folds away, leaving its icon.</summary>
public sealed class CompactCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is Breakpoint.Compact ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True in a compact window (below 1280 DIP): buttons fold to their icons.</summary>
public sealed class IsCompactConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is Breakpoint.Compact;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
