using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Navigation;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Domain;
using ContractSortKeys = Lifestream.Contracts.SortKeys;

namespace Lifestream.Client.Features.Donors;

/// <summary>The Donors screen: a paged, filterable list with the selected donor's detail beside it and the editor in a drawer (FEAT-01 to FEAT-08).</summary>
public sealed class DonorsViewModel : SortedRecordListViewModel<DonorListItem, DonorDetail>
{
    private const string EligibleKey = "eligible";
    private const string LastDonatedKey = "lastDonated";

    private readonly ILifestreamApiClient api;
    private readonly INavigationService navigation;
    private FilterOption<bool> _eligibilityFilter;
    private FilterOption<DonationRecency> _lastDonatedFilter;
    private (int DonorId, DonorDonations? History)? _fetchedHistory;
    private (int DonorId, int? Appointments)? _fetchedAppointments;

    public DonorsViewModel(ILifestreamApiClient api, IListViewStateStore states, IToastService toasts, IDialogService dialogs, INavigationService navigation, IUiDispatcher ui)
        : base(states, "donors", toasts, dialogs, ui)
    {
        this.api = api;
        this.navigation = navigation;
        EligibilityOptions = [new("All donors", null), new("Eligible", true), new("Not eligible", false)];
        LastDonatedOptions =
        [
            new("Any time", null),
            new("In the last 3 months", DonationRecency.Within3Months),
            new("3 to 12 months ago", DonationRecency.Within12Months),
            new("Over a year ago", DonationRecency.Over12Months),
            new("Never", DonationRecency.Never),
        ];
        _eligibilityFilter = EligibilityOptions[0];
        _lastDonatedFilter = LastDonatedOptions[0];
        if (ViewState.Filters.TryGetValue(EligibleKey, out var savedEligible) && bool.TryParse(savedEligible, out var eligible))
        {
            _eligibilityFilter = EligibilityOptions.First(o => o.Value == eligible);
        }

        if (ViewState.Filters.TryGetValue(LastDonatedKey, out var savedRecency) && Enum.TryParse<DonationRecency>(savedRecency, out var recency))
        {
            _lastDonatedFilter = LastDonatedOptions.First(o => o.Value == recency);
        }

        ShowAllHistoryCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => ShowAllHistory = !ShowAllHistory);
        BookAppointmentCommand = Command(
            _ => Detail is { } donor ? this.navigation.NavigateTo<AppointmentsViewModel>(new RelatedRecord(RecordKind.Donor, donor.Id, donor.Name)) : Task.CompletedTask,
            () => Detail is not null && !IsEditing);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Detail) or nameof(Editor))
            {
                BookAppointmentCommand.RaiseCanExecuteChanged();
            }

            if (e.PropertyName is nameof(Detail))
            {
                OnPropertyChanged(nameof(DetailCode));
                OnPropertyChanged(nameof(DetailSubtitle));
                OnPropertyChanged(nameof(DetailBloodGroup));
                OnPropertyChanged(nameof(DetailInitials));
            }

            if (e.PropertyName is nameof(Detail) or nameof(DetailFacts))
            {
                OnPropertyChanged(nameof(EligibilityCard));
                OnPropertyChanged(nameof(PanelFacts));
                ShowHistory();
            }
        };
    }

    /// <summary>The Eligibility chip: everyone, only donors who may donate today, or only those who may not. It applies at once.</summary>
    public IReadOnlyList<FilterOption<bool>> EligibilityOptions { get; }

    public FilterOption<bool> EligibilityFilter
    {
        get => _eligibilityFilter;
        set
        {
            if (SetProperty(ref _eligibilityFilter, value))
            {
                ViewState.Filters[EligibleKey] = value.Value?.ToString();
                FilterChanged();
            }
        }
    }

    /// <summary>The Last donated chip: any time, or a window counted back from today (or never). It applies at once.</summary>
    public IReadOnlyList<FilterOption<DonationRecency>> LastDonatedOptions { get; }

    public FilterOption<DonationRecency> LastDonatedFilter
    {
        get => _lastDonatedFilter;
        set
        {
            if (SetProperty(ref _lastDonatedFilter, value))
            {
                ViewState.Filters[LastDonatedKey] = value.Value?.ToString();
                FilterChanged();
            }
        }
    }

    protected override bool HasExtraFilters => base.HasExtraFilters || _eligibilityFilter.Value is not null || _lastDonatedFilter.Value is not null;

    protected override void ResetExtraFilters()
    {
        base.ResetExtraFilters();
        _eligibilityFilter = EligibilityOptions[0];
        _lastDonatedFilter = LastDonatedOptions[0];
        OnPropertyChanged(nameof(EligibilityFilter));
        OnPropertyChanged(nameof(LastDonatedFilter));
    }

    /// <summary>The selected donor's donations, newest first (design system 7.6 step 4): the date, the bag and where it went. Empty until loaded.</summary>
    public IReadOnlyList<DonationHistoryRow> HistoryRows { get; private set; } = [];

    /// <summary>The rows shown: the first <see cref="HistoryPreviewCount"/>, or all that were loaded after "Show all".</summary>
    public IReadOnlyList<DonationHistoryRow> VisibleHistory => ShowAllHistory ? HistoryRows : [.. HistoryRows.Take(HistoryPreviewCount)];

    /// <summary>How many history rows show before "Show all".</summary>
    public const int HistoryPreviewCount = 4;

    public bool ShowAllHistory
    {
        get => _showAllHistory;
        private set
        {
            if (SetProperty(ref _showAllHistory, value))
            {
                OnPropertyChanged(nameof(VisibleHistory));
                OnPropertyChanged(nameof(HistoryMoreText));
            }
        }
    }

    private bool _showAllHistory;

    /// <summary>"Show all 11" or "Show fewer"; null when every row already shows.</summary>
    public string? HistoryMoreText => HistoryRows.Count <= HistoryPreviewCount ? null : ShowAllHistory ? "Show fewer" : $"Show all {HistoryRows.Count}";

    /// <summary>Toggles between the first rows of the history and all the rows loaded. No API call.</summary>
    public System.Windows.Input.ICommand ShowAllHistoryCommand { get; }

    /// <summary>A line under the history: that only the latest are listed, that there are none, or that it could not be loaded. Null when nothing needs saying.</summary>
    public string? HistoryNote { get; private set; }

    /// <summary>True while the panel has a history section to show (rows or a note).</summary>
    public bool HasHistorySection => HistoryRows.Count > 0 || HistoryNote is not null;

    private void ShowHistory()
    {
        HistoryRows = [];
        HistoryNote = null;
        ShowAllHistory = false;
        if (Detail is { } donor && _fetchedHistory is { } fetched && fetched.DonorId == donor.Id)
        {
            if (fetched.History is not { } history)
            {
                HistoryNote = "The donation history couldn't be loaded. Select the donor again to retry.";
            }
            else
            {
                HistoryRows = DonationHistory.Rows(history, Today, Clock.LocalTimeZone);
                HistoryNote = history.Total == 0 ? "No donations yet."
                    : history.Total > history.Items.Count ? $"The latest {history.Items.Count} of {history.Total} donations are listed."
                    : null;
            }
        }

        OnPropertyChanged(nameof(HistoryRows));
        OnPropertyChanged(nameof(VisibleHistory));
        OnPropertyChanged(nameof(HistoryMoreText));
        OnPropertyChanged(nameof(HistoryNote));
        OnPropertyChanged(nameof(HasHistorySection));
    }

    /// <summary>"Book appointment" on the selected donor: opens the booking form on the Appointments screen with this donor already chosen (FEAT-04).</summary>
    public AsyncRelayCommand BookAppointmentCommand { get; }

    public override string PageTitle => "Donors";

    /// <summary>Today in the clinic's local time: the list works out each donor's eligibility for this day (<see cref="DonorStanding"/>).</summary>
    public DateOnly Today => DateOnly.FromDateTime(Clock.GetLocalNow().DateTime);

    /// <summary>The side panel's overline, "Donor D-0175".</summary>
    public string? DetailCode => Detail is { } d ? "Donor " + DonorStanding.Code(d.Id) : null;

    /// <summary>The line under the donor's name in the side panel: "Regular donor, age 42".</summary>
    public string? DetailSubtitle => Detail is { } d
        ? $"{(d.Frequency == DonorFrequency.Regular ? "Regular" : "Occasional")} donor, age {Eligibility.AgeOn(d.DateOfBirth, Today)}"
        : null;

    /// <summary>The donor's blood group with a true minus sign, for the large tag in the side panel.</summary>
    public string? DetailBloodGroup => Detail?.BloodType.ToTypographic();

    public string? DetailInitials => Detail is { } d ? PersonInitials(d.Name) : null;

    /// <summary>
    /// The eligibility card at the top of the side panel, from the API's answer (the "Eligibility" line of the detail) with the dates of the donor's record:
    /// eligible (success), waiting or on hold (warning), or not checked (neutral). Null while no donor is loaded.
    /// </summary>
    public EligibilityCard? EligibilityCard
    {
        get
        {
            if (Detail is not { } donor || DetailFacts.FirstOrDefault(f => f.Label == EligibilityLabel) is not { } fact)
            {
                return null;
            }

            var standing = DonorStanding.Of(donor.DateOfBirth, donor.LastDonated, donor.MedicalHold, Today);
            if (fact.Value == EligibleText)
            {
                return new(EligibilityCardKind.Eligible, EligibleText, donor.LastDonated is { } last
                    ? $"Last donated {Formatting.Date(last)}, {Today.DayNumber - last.DayNumber} days ago."
                    : "No donations yet.");
            }

            if (!fact.Value.StartsWith(NotEligiblePrefix, StringComparison.Ordinal))
            {
                return new(EligibilityCardKind.Unknown, "Eligibility not checked", "The server could not check it right now. Select the donor again to retry.");
            }

            // The reason is always said: the wait until a date, the hold, or the age rule (the list's "Not eligible" pill points here).
            var reasons = fact.Value[NotEligiblePrefix.Length..];
            return standing.Kind switch
            {
                DonorStandingKind.Waiting => new(EligibilityCardKind.Waiting, $"Can donate again on {Formatting.Date(standing.NextDate)}", reasons),
                DonorStandingKind.OnHold => new(EligibilityCardKind.Waiting, "On medical hold", reasons),
                _ => new(EligibilityCardKind.NotEligible, "Not eligible to donate", reasons),
            };
        }
    }

    /// <summary>
    /// The facts under "Details" in the side panel: the detail lines without those the panel already shows (blood group and how often they donate in the header, eligibility
    /// and the last donation in its card).
    /// </summary>
    public IReadOnlyList<DetailFact> PanelFacts => [.. DetailFacts.Where(f => f.Label is not (EligibilityLabel or "Blood group" or "Donates" or "Last donated"))];

    private const string EligibilityLabel = "Eligibility";
    private const string EligibleText = "Eligible to donate";
    private const string NotEligiblePrefix = "Not eligible: ";

    /// <summary>"AM" for "Adrian Mukherjee": the first letters of the first and last names.</summary>
    public static string PersonInitials(string name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
        };
    }

    protected override string PluralName => "donors";

    protected override string RecordType => "donor";

    protected override bool RecordIsPerson => true;

    /// <summary>The columns that sort, as the API's validator names them (defined once in the contracts).</summary>
    public override IReadOnlyList<string> SortKeys { get; } = ContractSortKeys.Donors;

    protected override Task<ApiResult<PagedResult<DonorListItem>>> FetchSortedAsync(ListRequest request, CancellationToken ct) =>
        api.GetDonorsAsync(new DonorQuery(request.Search, BloodTypeValue, request.Page, request.PageSize, request.Sort) { Eligible = _eligibilityFilter.Value, LastDonated = _lastDonatedFilter.Value }, ct);

    protected override int IdOf(DonorListItem item) => item.Id;

    protected override string NameOf(DonorListItem item) => item.Name;

    protected override int IdOfDetail(DonorDetail detail) => detail.Id;

    protected override string NameOfDetail(DonorDetail detail) => detail.Name;

    protected override Task<ApiResult<DonorDetail>> GetDetailAsync(int id, CancellationToken ct) => api.GetDonorAsync(id, ct);

    // The eligibility, the donation history and how many appointments the donor has on record, side by side; any may fail without failing the panel (it says so in
    // place; an unknown number of appointments leaves Delete to the server).
    protected override async Task<IReadOnlyList<DetailFact>> GetExtraFactsAsync(int id, CancellationToken ct)
    {
        var eligibilityCall = api.GetDonorEligibilityAsync(id, ct);
        var historyCall = api.GetDonorDonationsAsync(id, ct);
        var appointmentsCall = api.GetAppointmentsAsync(new Contracts.Appointments.AppointmentQuery(DonorId: id, PageSize: 1), ct); // only the total is read
        var result = await eligibilityCall.ConfigureAwait(false);
        var history = await historyCall.ConfigureAwait(false);
        var appointments = await appointmentsCall.ConfigureAwait(false);
        _fetchedHistory = (id, history.IsOk ? history.Value : null);
        _fetchedAppointments = (id, appointments.IsOk ? appointments.Value.Total : null);
        var donations = new DetailFact(DonationsLabel, history.IsOk ? DonationHistory.TotalText(history.Value) : "Could not be loaded right now");
        if (!result.IsOk)
        {
            return [new DetailFact(EligibilityLabel, "Could not be checked right now"), donations];
        }

        var eligibility = result.Value;
        return eligibility.IsEligible
            ? [new DetailFact(EligibilityLabel, EligibleText), donations]
            : [new DetailFact(EligibilityLabel, NotEligiblePrefix + string.Join(" ", eligibility.Reasons)), donations];
    }

    private const string DonationsLabel = "Donations";

    protected override IReadOnlyList<DetailFact> FactsOf(DonorDetail donor) =>
    [
        new("Date of birth", $"{Formatting.Date(donor.DateOfBirth)} (age {Eligibility.AgeOn(donor.DateOfBirth, DateOnly.FromDateTime(Clock.GetLocalNow().DateTime))})"),
        new("Gender", donor.Gender.ToString()),
        new("Blood group", donor.BloodType.ToDisplay()),
        new("Phone", string.IsNullOrEmpty(donor.Phone) ? "Not given" : donor.Phone),
        new("Address", string.IsNullOrEmpty(donor.Address) ? "Not given" : donor.Address),
        new("Donates", donor.Frequency.ToString()),
        new("Last donated", Formatting.Date(donor.LastDonated, "Never")),
    ];

    protected override Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct) => api.DeleteDonorAsync(id, ct);

    /// <summary>
    /// A donor with an appointment on record (booked, cancelled or with a donation) can't be deleted: the server keeps every appointment and the receipts and bags
    /// behind it, and they point at the donor. Delete is offered only for a donor with none (one added by mistake).
    /// </summary>
    protected override string? DeleteBlockOf(DonorDetail donor) =>
        _fetchedAppointments is { } fetched && fetched.DonorId == donor.Id && fetched.Appointments is > 0
            ? HistoryKept
            : null;

    private const string HistoryKept = "A donor with appointments or donations on record can't be deleted: their history is kept.";

    protected override string? DeleteRefusedText(string name) =>
        $"Donor '{name}' can't be deleted: they have appointments or donations on record, and that history is kept. Nothing was changed.";

    // The API keeps the deleted donor for a few seconds and puts it back as it was (same id, last-donation date, medical hold), so Undo restores rather than re-creates.
    protected override Task<ApiResult<DonorDetail>> RestoreAsync(DonorDetail d, CancellationToken ct) => api.RestoreDonorAsync(d.Id, ct);

    protected override RecordEditorViewModel CreateEditor(DonorDetail? existing) =>
        existing is null
            ? DonorEditorViewModel.ForNew(api, Toasts, Dialogs, Clock)
            : DonorEditorViewModel.ForEdit(api, Toasts, Dialogs, existing, Clock);
}

/// <summary>The look of the donor's eligibility card.</summary>
public enum EligibilityCardKind { Eligible, Waiting, Unknown, NotEligible }

/// <summary>The donor's eligibility card in the side panel: a headline and one line of detail.</summary>
public sealed record EligibilityCard(EligibilityCardKind Kind, string Headline, string Message);
