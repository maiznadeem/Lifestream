using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts;
using Lifestream.Contracts.Patients;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Patients;

/// <summary>The Patients screen: a paged, filterable list with the selected patient's detail beside it and the editor in a drawer (FEAT-01 to FEAT-08).</summary>
public sealed class PatientsViewModel(ILifestreamApiClient api, IListViewStateStore states, IToastService toasts, IDialogService dialogs)
    : RecordListViewModel<PatientListItem, PatientDetail>(states, "patients", toasts, dialogs)
{
    public override string PageTitle => "Patients";

    protected override string PluralName => "patients";

    protected override string RecordType => "patient";

    protected override Task<ApiResult<PagedResult<PatientListItem>>> FetchPageAsync(ListRequest request, CancellationToken ct) =>
        api.GetPatientsAsync(new PatientQuery(request.Search, BloodTypeValue, request.Page, request.PageSize, request.Sort), ct);

    protected override int IdOf(PatientListItem item) => item.Id;

    protected override string NameOf(PatientListItem item) => item.Name;

    protected override int IdOfDetail(PatientDetail detail) => detail.Id;

    protected override string NameOfDetail(PatientDetail detail) => detail.Name;

    protected override Task<ApiResult<PatientDetail>> GetDetailAsync(int id, CancellationToken ct) => api.GetPatientAsync(id, ct);

    protected override IReadOnlyList<DetailFact> FactsOf(PatientDetail patient) =>
    [
        new("Date of birth", $"{Formatting.Date(patient.DateOfBirth)} (age {Eligibility.AgeOn(patient.DateOfBirth, DateOnly.FromDateTime(Clock.GetLocalNow().DateTime))})"),
        new("Gender", patient.Gender.ToString()),
        new("Blood type", patient.BloodType.ToDisplay()),
        new("Phone", string.IsNullOrEmpty(patient.Phone) ? "Not given" : patient.Phone),
        new("Address", string.IsNullOrEmpty(patient.Address) ? "Not given" : patient.Address),
    ];

    protected override Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct) => api.DeletePatientAsync(id, ct);

    protected override Task<ApiResult<PatientDetail>> RestoreAsync(PatientDetail d, CancellationToken ct) =>
        api.CreatePatientAsync(new CreatePatientRequest(d.Name, d.DateOfBirth, d.Gender, d.BloodType, d.Phone, d.Address), ct);

    protected override RecordEditorViewModel CreateEditor(PatientDetail? existing) =>
        existing is null
            ? PatientEditorViewModel.ForNew(api, Toasts, Dialogs, Clock)
            : PatientEditorViewModel.ForEdit(api, Toasts, Dialogs, existing, Clock);
}
