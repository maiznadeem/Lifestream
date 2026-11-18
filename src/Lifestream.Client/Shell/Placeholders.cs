using Lifestream.Client.Common;

namespace Lifestream.Client.Shell.Placeholders;

/// <summary>A screen that is not built yet. It makes no API call and is replaced by the feature tasks.</summary>
public abstract class PlaceholderViewModel(string title) : ViewModelBase(LoadState.Ready)
{
    public override string PageTitle => title;

    public string Message => $"The {title} screen is built in a later step.";
}

public sealed class DashboardPlaceholderViewModel() : PlaceholderViewModel("Dashboard");

public sealed class DonorsPlaceholderViewModel() : PlaceholderViewModel("Donors");

public sealed class AppointmentsPlaceholderViewModel() : PlaceholderViewModel("Appointments");

public sealed class PatientsPlaceholderViewModel() : PlaceholderViewModel("Patients");

public sealed class RequestsPlaceholderViewModel() : PlaceholderViewModel("Requests");

public sealed class StockPlaceholderViewModel() : PlaceholderViewModel("Stock");

public sealed class ReceiptsPlaceholderViewModel() : PlaceholderViewModel("Receipts");

public sealed class StaffPlaceholderViewModel() : PlaceholderViewModel("Staff");

public sealed class ProfilePlaceholderViewModel() : PlaceholderViewModel("Profile");
