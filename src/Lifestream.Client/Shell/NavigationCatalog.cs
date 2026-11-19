using Lifestream.Client.Common;
using Lifestream.Contracts.Auth;
using Lifestream.Domain;

namespace Lifestream.Client.Shell;

/// <summary>Who may open a destination.</summary>
public enum Audience
{
    /// <summary>Every signed-in user.</summary>
    Everyone,

    /// <summary>Admins, and the demo account (which can view but not change staff).</summary>
    AdminOrDemo,
}

/// <summary>One sidebar entry. The title is also the page title and the window title suffix (SHELL-21, SHELL-23).</summary>
public sealed record Destination(string Title, string Group, string IconGlyph, Type ViewModelType, Audience Audience = Audience.Everyone)
{
    /// <summary>The vector icon of direction A (an IconKind name in the Desktop).</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>What the sidebar count of this destination counts ("still to come today"), for screen readers; empty when it has no count.</summary>
    public string BadgeNoun { get; init; } = string.Empty;

    public bool IsAllowedFor(SessionUser? user) => Audience switch
    {
        Audience.Everyone => user is not null,
        Audience.AdminOrDemo => user is { Role: Role.Admin } or { IsDemo: true },
        _ => false,
    };
}

/// <summary>
/// The sidebar: Dashboard on its own, then four groups, eight destinations (at most 8, SHELL-21). The feature tasks replace the placeholder view
/// model type of their entry with the real one; nothing else here changes.
/// </summary>
public static class NavigationCatalog
{
    // Segoe Fluent Icons code points.
    public static IReadOnlyList<Destination> Default { get; } =
    [
        new("Dashboard", "", "", typeof(Features.Dashboard.DashboardViewModel)) { Icon = "Home" },
        new("Donors", "Donations", "", typeof(Features.Donors.DonorsViewModel)) { Icon = "Donor" },
        new("Appointments", "Donations", "", typeof(Features.Appointments.AppointmentsViewModel)) { Icon = "Cal", BadgeNoun = "still to come today" },
        new("Patients", "Transfusions", "", typeof(Features.Patients.PatientsViewModel)) { Icon = "Patient" },
        new("Requests", "Transfusions", "", typeof(Features.Requests.RequestsViewModel)) { Icon = "Request", BadgeNoun = "open" },
        new("Stock", "Inventory", "", typeof(Features.Stock.StockViewModel)) { Icon = "Stock", BadgeNoun = "groups below minimum" },
        new("Receipts", "Inventory", "", typeof(Features.Receipts.ReceiptsViewModel)) { Icon = "Receipt" },
        new("Staff", "Administration", "", typeof(Features.Staff.StaffViewModel), Audience.AdminOrDemo) { Icon = "Staff" },
    ];

    /// <summary>Where "Profile" in the account menu goes.</summary>
    public static Type ProfileViewModelType { get; set; } = typeof(Features.Profile.ProfileViewModel);
}

/// <summary>Shown instead of a screen the user's role cannot use. The primary action goes back to the Dashboard; it never signs out (SHELL-22).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML, which needs instance properties.")]
public sealed partial class AccessDeniedViewModel : ViewModelBase, INavigationParameterReceiver
{
    public AccessDeniedViewModel()
        : base(LoadState.Ready)
    {
    }

    public override string PageTitle => "Access denied";

    public string Message => "Your account doesn't have access to this page. Ask an administrator if you need it.";

    public string PrimaryActionText => "Back to Dashboard";

    /// <summary>Navigates to the Dashboard. The shell hands it over as the navigation parameter, before the page is shown; the property also notifies, so a later change reaches the bound button.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial AsyncRelayCommand? BackToDashboardCommand { get; set; }

    public void ReceiveParameter(object parameter)
    {
        if (parameter is AsyncRelayCommand command)
        {
            BackToDashboardCommand = command;
        }
    }
}
