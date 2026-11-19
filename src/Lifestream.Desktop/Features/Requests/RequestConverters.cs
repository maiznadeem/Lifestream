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

/// <summary>The badge's icon for an urgency: an alert while it is critical or soon, none otherwise.</summary>
public sealed class UrgencyIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RequestUrgencyKind.Critical => IconKind.Alert,
        RequestUrgencyKind.Soon => IconKind.Clock,
        _ => IconKind.None,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
