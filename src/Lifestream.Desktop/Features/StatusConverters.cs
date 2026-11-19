using System.Globalization;
using System.Windows.Data;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Requests;
using Lifestream.Desktop.Controls;
using Lifestream.Domain;

namespace Lifestream.Desktop.Features;

/// <summary>An appointment status as the chip's icon and colour; a no-show reads as "incomplete" (a warning), the chip's own word is replaced by <see cref="AppointmentStatusTextConverter"/>.</summary>
public sealed class AppointmentStatusKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AppointmentStatus.Completed => StatusKind.Completed,
        AppointmentStatus.Cancelled => StatusKind.Cancelled,
        AppointmentStatus.NoShow => StatusKind.Incomplete,
        _ => StatusKind.Pending,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class AppointmentStatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AppointmentStatus status ? AppointmentsViewModel.StatusText(status) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A request status as the chip's icon and colour; "fulfilled" shares the completed look and keeps its own word.</summary>
public sealed class RequestStatusKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RequestStatus.Fulfilled => StatusKind.Completed,
        RequestStatus.Cancelled => StatusKind.Cancelled,
        _ => StatusKind.Pending,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RequestStatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is RequestStatus status ? RequestsViewModel.StatusText(status) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A UTC instant in the user's local time in the app format; date only when there is no time of day (FEAT-14).</summary>
public sealed class LocalWhenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is DateTime utc ? RecordFormatting.When(utc) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#42": an id as the secondary text under a name.</summary>
public sealed class IdTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int id ? RecordFormatting.IdText(id) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
