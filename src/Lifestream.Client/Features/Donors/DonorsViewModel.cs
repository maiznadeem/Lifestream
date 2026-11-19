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
        BookAppointmentCommand = new AsyncRelayCommand(
            _ => Detail is { } donor ? this.navigation.NavigateTo<AppointmentsViewModel>(new RelatedRecord(RecordKind.Donor, donor.Id, donor.Name)) : Task.CompletedTask,
            () => Detail is not null && !IsEditing);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Detail) or nameof(Editor))
            {
                BookAppointmentCommand.RaiseCanExecuteChanged();
            }
        };
    }

    /// <summary>"Book appointment" on the selected donor: opens the booking form on the Appointments screen with this donor already chosen (FEAT-04).</summary>
    public AsyncRelayCommand BookAppointmentCommand { get; }

    public override string PageTitle => "Donors";

    protected override string PluralName => "donors";

    protected override string RecordType => "donor";

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
            return [new DetailFact("Eligibility", "Could not be checked right now")];
        }

        var eligibility = result.Value;
        return eligibility.IsEligible
            ? [new DetailFact("Eligibility", "Eligible to donate")]
            : [new DetailFact("Eligibility", "Not eligible: " + string.Join(" ", eligibility.Reasons))];
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
