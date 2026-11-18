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
    public bool IsAllowedFor(SessionUser? user) => Audience switch
    {
        Audience.Everyone => user is not null,
        Audience.AdminOrDemo => user is { Role: Role.Admin } or { IsDemo: true },
        _ => false,
    };
}

/// <summary>
/// The sidebar: five groups, eight destinations (at most 8, SHELL-21). The feature tasks replace the placeholder view
/// model type of their entry with the real one; nothing else here changes.
/// </summary>
public static class NavigationCatalog
{
    // Segoe Fluent Icons code points.
    public static IReadOnlyList<Destination> Default { get; } =
    [
        new("Dashboard", "Overview", "", typeof(Placeholders.DashboardPlaceholderViewModel)),
        new("Donors", "Donations", "", typeof(Placeholders.DonorsPlaceholderViewModel)),
        new("Appointments", "Donations", "", typeof(Placeholders.AppointmentsPlaceholderViewModel)),
        new("Patients", "Transfusions", "", typeof(Placeholders.PatientsPlaceholderViewModel)),
        new("Requests", "Transfusions", "", typeof(Placeholders.RequestsPlaceholderViewModel)),
        new("Stock", "Inventory", "", typeof(Placeholders.StockPlaceholderViewModel)),
        new("Receipts", "Inventory", "", typeof(Placeholders.ReceiptsPlaceholderViewModel)),
        new("Staff", "Administration", "", typeof(Placeholders.StaffPlaceholderViewModel), Audience.AdminOrDemo),
    ];

    /// <summary>Where "Profile" in the account menu goes.</summary>
    public static Type ProfileViewModelType { get; set; } = typeof(Placeholders.ProfilePlaceholderViewModel);
}

/// <summary>Shown instead of a screen the user's role cannot use. The primary action goes back to the Dashboard; it never signs out (SHELL-22).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML, which needs instance properties.")]
public sealed class AccessDeniedViewModel : ViewModelBase
{
    public AccessDeniedViewModel()
        : base(LoadState.Ready)
    {
    }

    public override string PageTitle => "Access denied";

    public string Message => "Your account doesn't have access to this page. Ask an administrator if you need it.";

    public string PrimaryActionText => "Back to Dashboard";

    /// <summary>Set by the shell: navigates to the Dashboard.</summary>
    public AsyncRelayCommand? BackToDashboardCommand { get; set; }
}
