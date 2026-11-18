using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Donors;

/// <summary>The donor form: new, or pre-filled from a record where a change sends only what changed (FEAT-05, FEAT-07, FEAT-08).</summary>
public sealed partial class DonorEditorViewModel : PersonEditorViewModel
{
    private readonly ILifestreamApiClient _api;
    private readonly int? _id;
    private string _rowVersion = string.Empty;
    private DonorFrequency? _baselineFrequency;
    private DonorDetail? _reloaded;

    private DonorEditorViewModel(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, TimeProvider clock, DonorDetail? existing)
        : base(toasts, dialogs, clock, "donor", existing is null, Eligibility.MinAgeYears)
    {
        _api = api;
        _id = existing?.Id;
        FrequencyChoices = [.. Enum.GetValues<DonorFrequency>().Select(f => new Choice<DonorFrequency>(f.ToString(), f))];
        DeclareFormFields(nameof(Name), nameof(DateOfBirth), nameof(Gender), nameof(BloodType), nameof(Phone), nameof(Address), nameof(Frequency));
        if (existing is not null)
        {
            Load(existing);
        }
    }

    public static DonorEditorViewModel ForNew(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, TimeProvider? clock = null) =>
        new(api, toasts, dialogs, clock ?? TimeProvider.System, null);

    public static DonorEditorViewModel ForEdit(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, DonorDetail donor, TimeProvider? clock = null) =>
        new(api, toasts, dialogs, clock ?? TimeProvider.System, donor);

    public IReadOnlyList<Choice<DonorFrequency>> FrequencyChoices { get; }

    [ObservableProperty]
    public partial DonorFrequency? Frequency { get; set; }

    protected override bool ExtraDirty => Frequency != _baselineFrequency;

    private void Load(DonorDetail donor)
    {
        _rowVersion = donor.RowVersion;
        LoadValues(new PersonValues(donor.Name, donor.DateOfBirth, donor.Gender, donor.BloodType, donor.Phone, donor.Address));
        Frequency = donor.Frequency;
        _baselineFrequency = donor.Frequency;
    }

    protected override void AcceptExtraAsBaseline() => _baselineFrequency = Frequency;

    protected override string? ValidateExtraField(string field) =>
        field == nameof(Frequency) && Frequency is null ? "Choose how often the donor gives blood." : null;

    partial void OnFrequencyChanged(DonorFrequency? value) => FieldEdited(nameof(Frequency));

    /// <summary>The PATCH body of an edit: the row version and only the fields that differ from what was loaded (FEAT-05).</summary>
    public UpdateDonorRequest BuildUpdate()
    {
        var now = CurrentValues();
        var was = Baseline;
        return new UpdateDonorRequest(
            _rowVersion,
            Name: now.Name == was.Name ? default : Optional.Of<string>(now.Name),
            DateOfBirth: now.DateOfBirth == was.DateOfBirth ? default : Optional.Of<DateOnly>(now.DateOfBirth!.Value),
            Gender: now.Gender == was.Gender ? default : Optional.Of<Gender>(now.Gender!.Value),
            BloodType: now.BloodType == was.BloodType ? default : Optional.Of<BloodType>(now.BloodType!.Value),
            Phone: OptionalTextIfChanged(now.Phone, was.Phone),
            Address: OptionalTextIfChanged(now.Address, was.Address),
            Frequency: Frequency == _baselineFrequency ? default : Optional.Of<DonorFrequency>(Frequency!.Value));
    }

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareSubmit()
    {
        if (_id is { } id)
        {
            var update = BuildUpdate();
            return async ct =>
            {
                var result = await _api.UpdateDonorAsync(id, update, ct).ConfigureAwait(false);
                return Finish(result);
            };
        }

        var v = CurrentValues();
        var create = new CreateDonorRequest(v.Name, v.DateOfBirth!.Value, v.Gender!.Value, v.BloodType!.Value, v.Phone, v.Address, Frequency!.Value);
        return async ct =>
        {
            var result = await _api.CreateDonorAsync(create, ct).ConfigureAwait(false);
            return Finish(result);
        };
    }

    private ApiProblem? Finish(ApiResult<DonorDetail> result)
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
            var result = await _api.GetDonorAsync(id, ct).ConfigureAwait(false);
            _reloaded = result.IsOk ? result.Value : null;
            return result.Problem;
        };
    }

    protected override void ApplyReloaded()
    {
        if (_reloaded is { } donor)
        {
            Load(donor);
        }
    }
}
