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
    private readonly ILifestreamApiClient api;
    private readonly INavigationService navigation;

    public DonorsViewModel(ILifestreamApiClient api, IListViewStateStore states, IToastService toasts, IDialogService dialogs, INavigationService navigation, IUiDispatcher ui)
        : base(states, "donors", toasts, dialogs, ui)
    {
        this.api = api;
        this.navigation = navigation;
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
            }
        };
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

            var reasons = fact.Value[NotEligiblePrefix.Length..];
            return standing.Kind switch
            {
                DonorStandingKind.Waiting => new(EligibilityCardKind.Waiting, $"Can donate again on {Formatting.Date(standing.NextDate)}", reasons),
                DonorStandingKind.OnHold => new(EligibilityCardKind.Waiting, "On medical hold", reasons),
                _ => new(EligibilityCardKind.Waiting, "Not eligible to donate", reasons),
            };
        }
    }

    /// <summary>
    /// The facts under "Details" in the side panel: the detail lines without those the panel already shows (blood group and how often they donate in the header, eligibility
    /// and the last donation in its card).
    /// </summary>
    public IReadOnlyList<DetailFact> PanelFacts => [.. DetailFacts.Where(f => f.Label is not (EligibilityLabel or "Blood type" or "Donates" or "Last donated"))];

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
        api.GetDonorsAsync(new DonorQuery(request.Search, BloodTypeValue, request.Page, request.PageSize, request.Sort), ct);

    protected override int IdOf(DonorListItem item) => item.Id;

    protected override string NameOf(DonorListItem item) => item.Name;

    protected override int IdOfDetail(DonorDetail detail) => detail.Id;

    protected override string NameOfDetail(DonorDetail detail) => detail.Name;

    protected override Task<ApiResult<DonorDetail>> GetDetailAsync(int id, CancellationToken ct) => api.GetDonorAsync(id, ct);

    protected override async Task<IReadOnlyList<DetailFact>> GetExtraFactsAsync(int id, CancellationToken ct)
    {
        var result = await api.GetDonorEligibilityAsync(id, ct).ConfigureAwait(false);
        if (!result.IsOk)
        {
            return [new DetailFact(EligibilityLabel, "Could not be checked right now")];
        }

        var eligibility = result.Value;
        return eligibility.IsEligible
            ? [new DetailFact(EligibilityLabel, EligibleText)]
            : [new DetailFact(EligibilityLabel, NotEligiblePrefix + string.Join(" ", eligibility.Reasons))];
    }

    protected override IReadOnlyList<DetailFact> FactsOf(DonorDetail donor) =>
    [
        new("Date of birth", $"{Formatting.Date(donor.DateOfBirth)} (age {Eligibility.AgeOn(donor.DateOfBirth, DateOnly.FromDateTime(Clock.GetLocalNow().DateTime))})"),
        new("Gender", donor.Gender.ToString()),
        new("Blood type", donor.BloodType.ToDisplay()),
        new("Phone", string.IsNullOrEmpty(donor.Phone) ? "Not given" : donor.Phone),
        new("Address", string.IsNullOrEmpty(donor.Address) ? "Not given" : donor.Address),
        new("Donates", donor.Frequency.ToString()),
        new("Last donated", Formatting.Date(donor.LastDonated, "Never")),
    ];

    protected override Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct) => api.DeleteDonorAsync(id, ct);

    // The API keeps the deleted donor for a few seconds and puts it back as it was (same id, last-donation date, medical hold), so Undo restores rather than re-creates.
    protected override Task<ApiResult<DonorDetail>> RestoreAsync(DonorDetail d, CancellationToken ct) => api.RestoreDonorAsync(d.Id, ct);

    protected override RecordEditorViewModel CreateEditor(DonorDetail? existing) =>
        existing is null
            ? DonorEditorViewModel.ForNew(api, Toasts, Dialogs, Clock)
            : DonorEditorViewModel.ForEdit(api, Toasts, Dialogs, existing, Clock);
}

/// <summary>The look of the donor's eligibility card.</summary>
public enum EligibilityCardKind { Eligible, Waiting, Unknown }

/// <summary>The donor's eligibility card in the side panel: a headline and one line of detail.</summary>
public sealed record EligibilityCard(EligibilityCardKind Kind, string Headline, string Message);
