using Lifestream.Domain;

namespace Lifestream.Client.Features.Requests;

/// <summary>How soon a pending request needs its blood, as the badge in the list and the side panel shows it.</summary>
public enum RequestUrgencyKind
{
    /// <summary>Fulfilled or cancelled: nothing to do.</summary>
    None,

    /// <summary>More than three days left.</summary>
    Normal,

    /// <summary>Needed within three days (tomorrow to three days from now).</summary>
    Soon,

    /// <summary>Needed today, or the date has passed.</summary>
    Critical,
}

/// <summary>A request's urgency today: the kind and the short text of its badge ("Overdue 2 days", "Today", "In 2 days", "In 9 days").</summary>
public sealed record RequestUrgency(RequestUrgencyKind Kind, string Text)
{
    /// <summary>Days left at or below which a request is "soon".</summary>
    public const int SoonDays = 3;

    public static RequestUrgency Of(RequestStatus status, DateOnly requiredBy, DateOnly today)
    {
        if (status != RequestStatus.Pending)
        {
            return new(RequestUrgencyKind.None, string.Empty);
        }

        var days = requiredBy.DayNumber - today.DayNumber;
        return days switch
        {
            < -1 => new(RequestUrgencyKind.Critical, $"Overdue {-days} days"),
            -1 => new(RequestUrgencyKind.Critical, "Overdue 1 day"),
            0 => new(RequestUrgencyKind.Critical, "Today"),
            1 => new(RequestUrgencyKind.Soon, "Tomorrow"),
            <= SoonDays => new(RequestUrgencyKind.Soon, $"In {days} days"),
            _ => new(RequestUrgencyKind.Normal, $"In {days} days"),
        };
    }
}
