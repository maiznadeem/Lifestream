using System.Globalization;
using System.Windows.Data;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts.Appointments;

namespace Lifestream.Desktop.Features.Appointments;

/// <summary>The day group of an appointment row (its local date in the screen's zone), for the list's grouping. Made by the view with its view model.</summary>
public sealed class AppointmentDayConverter(AppointmentsViewModel model) : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is AppointmentListItem item ? model.DayOf(item) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A day group's heading ("Today, Thu 19 Nov") from the group's day and the view model (a multi-binding).</summary>
public sealed class DayHeadingConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [DateOnly day, AppointmentsViewModel model, ..] ? model.DayHeading(day) : string.Empty;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True for today's group (a multi-binding of the group's day and today).</summary>
public sealed class IsTodayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values is [DateOnly day, DateOnly today, ..] && day == today;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"3 appointments" under a day group.</summary>
public sealed class AppointmentCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int n ? (n == 1 ? "1 appointment" : $"{n} appointments") : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>An appointment's local time of day, "11:00", from its UTC start and the screen's zone (a multi-binding).</summary>
public sealed class SlotTimeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [DateTime utc, TimeZoneInfo zone, ..] ? RecordFormatting.LocalTime(utc, zone).ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The local date of a UTC instant in the screen's zone, "18 Nov 2026" (a multi-binding of the instant and the zone).</summary>
public sealed class UtcDayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [DateTime utc, TimeZoneInfo zone, ..] ? Lifestream.Client.Common.Formatting.Date(RecordFormatting.LocalDate(utc, zone)) : string.Empty;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
