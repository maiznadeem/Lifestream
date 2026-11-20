using System.Globalization;
using System.Windows.Data;
using Lifestream.Client.Features.Requests;
using Lifestream.Desktop.Controls;
using Lifestream.Domain;

namespace Lifestream.Desktop.Features.Requests;

/// <summary>
/// A part of a request row's urgency today (a multi-binding of its status, its needed-by date and the view model), chosen by the parameter: "Text", "Kind" (the urgency
/// kind; the badge converters turn it into a colour and an icon) or "Visibility" (collapsed when the request is not pending).
/// </summary>
public sealed class RequestUrgencyConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var urgency = values is [RequestStatus status, DateOnly requiredBy, RequestsViewModel model, ..] ? model.UrgencyOf(status, requiredBy) : new RequestUrgency(RequestUrgencyKind.None, string.Empty);
        return (parameter as string) switch
        {
            "Kind" => urgency.Kind,
            "Visibility" => urgency.Kind == RequestUrgencyKind.None ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible,
            _ => urgency.Text,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The Cover of a request row (a multi-binding of the row and the view model; <see cref="RequestsViewModel.CoverOf(Contracts.Requests.RequestListItem)"/>), chosen by the
/// parameter: "Text" ("Can fulfil", "2 short", "No stock"), "Kind" (green, amber, red), "Icon" or "Visibility" (collapsed for a closed request or unknown stock). The
/// stock is read when the row is put in place, which happens after the stock has been applied.
/// </summary>
public sealed class RequestCoverConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var cover = values is [Contracts.Requests.RequestListItem item, RequestsViewModel model, ..] ? model.CoverOf(item) : null;
        return CoverParts.Of(cover, parameter as string);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A <see cref="RequestCoverage"/> as a part of its pill, chosen by the parameter (see <see cref="RequestCoverConverter"/>).</summary>
public sealed class CoverPartConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => CoverParts.Of(value as RequestCoverage, parameter as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

internal static class CoverParts
{
    public static object Of(RequestCoverage? cover, string? part) => part switch
    {
        "Kind" => cover?.Kind switch
        {
            CoverKind.CanFulfil => BadgeKind.Success,
            CoverKind.Partial => BadgeKind.Warning,
            CoverKind.NoStock => BadgeKind.Critical,
            _ => BadgeKind.Neutral,
        },
        "Icon" => cover?.Kind switch
        {
            CoverKind.CanFulfil => IconKind.Check,
            CoverKind.Partial or CoverKind.NoStock => IconKind.Alert,
            _ => IconKind.None,
        },
        "Visibility" => cover is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible,
        _ => cover?.Text ?? string.Empty,
    };
}

/// <summary>An urgency as the badge colour: needed today or overdue critical, within three days warning, later neutral.</summary>
public sealed class UrgencyBadgeKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RequestUrgencyKind.Critical => BadgeKind.Critical,
        RequestUrgencyKind.Soon => BadgeKind.Warning,
        _ => BadgeKind.Neutral,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The badge's icon for an urgency: an alert when critical, a clock when soon, a calendar for a later date (every pill has an icon), none when not pending.</summary>
public sealed class UrgencyIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RequestUrgencyKind.Critical => IconKind.Alert,
        RequestUrgencyKind.Soon => IconKind.Clock,
        RequestUrgencyKind.Normal => IconKind.Cal,
        _ => IconKind.None,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
