using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts;
using Lifestream.Contracts.Patients;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Patients;

/// <summary>The patient form: new, or pre-filled from a record where a change sends only what changed (FEAT-05, FEAT-07, FEAT-08).</summary>
public sealed class PatientEditorViewModel : PersonEditorViewModel
{
    private readonly ILifestreamApiClient _api;
    private readonly int? _id;
    private string _rowVersion = string.Empty;
    private PatientDetail? _reloaded;

    private PatientEditorViewModel(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, TimeProvider clock, PatientDetail? existing)
        : base(toasts, dialogs, clock, "patient", existing is null, 0)
    {
        _api = api;
        _id = existing?.Id;
        DeclareFormFields(nameof(Name), nameof(DateOfBirth), nameof(Gender), nameof(BloodType), nameof(Phone), nameof(Address));
        if (existing is not null)
        {
            Load(existing);
        }
    }

    public static PatientEditorViewModel ForNew(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, TimeProvider? clock = null) =>
        new(api, toasts, dialogs, clock ?? TimeProvider.System, null);

    public static PatientEditorViewModel ForEdit(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, PatientDetail patient, TimeProvider? clock = null) =>
        new(api, toasts, dialogs, clock ?? TimeProvider.System, patient);

    private void Load(PatientDetail patient)
    {
        _rowVersion = patient.RowVersion;
        LoadValues(new PersonValues(patient.Name, patient.DateOfBirth, patient.Gender, patient.BloodType, patient.Phone, patient.Address));
    }

    /// <summary>The PATCH body of an edit: the row version and only the fields that differ from what was loaded (FEAT-05).</summary>
    public UpdatePatientRequest BuildUpdate()
    {
        var now = CurrentValues();
        var was = Baseline;
        return new UpdatePatientRequest(
            _rowVersion,
            Name: now.Name == was.Name ? default : Optional.Of<string>(now.Name),
            DateOfBirth: now.DateOfBirth == was.DateOfBirth ? default : Optional.Of<DateOnly>(now.DateOfBirth!.Value),
            Gender: now.Gender == was.Gender ? default : Optional.Of<Gender>(now.Gender!.Value),
            BloodType: now.BloodType == was.BloodType ? default : Optional.Of<BloodType>(now.BloodType!.Value),
            Phone: OptionalTextIfChanged(now.Phone, was.Phone),
            Address: OptionalTextIfChanged(now.Address, was.Address));
    }

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareSubmit()
    {
        if (_id is { } id)
        {
            var update = BuildUpdate();
            return async ct =>
            {
                var result = await _api.UpdatePatientAsync(id, update, ct).ConfigureAwait(false);
                return Finish(result);
            };
        }

        var v = CurrentValues();
        var create = new CreatePatientRequest(v.Name, v.DateOfBirth!.Value, v.Gender!.Value, v.BloodType!.Value, v.Phone, v.Address);
        return async ct =>
        {
            var result = await _api.CreatePatientAsync(create, ct).ConfigureAwait(false);
            return Finish(result);
        };
    }

    private ApiProblem? Finish(ApiResult<PatientDetail> result)
    {
        if (!result.IsOk)
        {
            return result.Problem;
        }

        SavedRecord = result.Value;
        _rowVersion = result.Value.RowVersion;
        return null;
    }

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareReload()
    {
        var id = _id ?? 0;
        return async ct =>
        {
            var result = await _api.GetPatientAsync(id, ct).ConfigureAwait(false);
            _reloaded = result.IsOk ? result.Value : null;
            return result.Problem;
        };
    }

    protected override void ApplyReloaded()
    {
        if (_reloaded is { } patient)
        {
            Load(patient);
        }
    }
}
