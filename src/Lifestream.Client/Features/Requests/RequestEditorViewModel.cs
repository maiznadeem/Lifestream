using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Requests;

namespace Lifestream.Client.Features.Requests;

/// <summary>
/// The request form: new (the patient from a record picker, the units, the date the blood is needed by) or an edit of a pending request, where only
/// the units and the date can change and only what changed is sent. Nothing starts with a made-up value.
/// </summary>
public sealed partial class RequestEditorViewModel : RecordEditorViewModel
{
    /// <summary>The most units one request may ask for (the API's setting <c>Clinic:MaxRequestUnits</c>, which defaults to 10).</summary>
    public const int MaxUnits = 10;

    private readonly ILifestreamApiClient _api;
    private readonly int? _id;
    private string _rowVersion = string.Empty;
    private int? _baselineUnits;
    private DateOnly? _baselineRequiredBy;
    private RequestDetail? _reloaded;

    private RequestEditorViewModel(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, TimeProvider clock, RequestDetail? existing)
        : base(toasts, dialogs, clock, "request", existing is null)
    {
        _api = api;
        _id = existing?.Id;
        DeclareFormFields(nameof(PatientId), nameof(UnitsText), nameof(RequiredBy));
        if (existing is not null)
        {
            Load(existing);
        }
    }

    public static RequestEditorViewModel ForNew(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, TimeProvider? clock = null) =>
        new(api, toasts, dialogs, clock ?? TimeProvider.System, null);

    public static RequestEditorViewModel ForEdit(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, RequestDetail request, TimeProvider? clock = null) =>
        new(api, toasts, dialogs, clock ?? TimeProvider.System, request);

    public new string Title => IsNew ? "New blood request" : "Edit blood request";

    public new string SaveText => IsNew ? "Add request" : "Save changes";

    /// <summary>A request is the patient's: "Request for 'Omar Haddad' added."</summary>
    protected override string SavedMessage => $"Request for '{RecordName}' {(IsNew ? "added" : "saved")}.";

    public bool ShowPatientPicker => IsNew;

    /// <summary>The earliest "required by" date: today.</summary>
    public DateOnly EarliestRequiredBy => LocalToday;

    /// <summary>The latest "required by" date the API accepts: one year from today.</summary>
    public DateOnly LatestRequiredBy => LocalToday.AddYears(1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatientId))]
    public partial RecordOption? PatientOption { get; set; }

    public int? PatientId => PatientOption?.Id;

    /// <summary>The patient's name: read-only text when editing.</summary>
    [ObservableProperty]
    public partial string PatientName { get; set; } = string.Empty;

    /// <summary>The units as typed: digits only.</summary>
    [ObservableProperty]
    public partial string UnitsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateOnly? RequiredBy { get; set; }

    private int? Units => int.TryParse(UnitsText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    public override bool HasUnsavedChanges => IsNew
        ? PatientOption is not null || UnitsText.Trim().Length > 0 || RequiredBy is not null
        : Units != _baselineUnits || RequiredBy != _baselineRequiredBy;

    protected override string RecordName => string.IsNullOrEmpty(PatientName) ? PatientOption?.Title ?? "request" : PatientName;

    private void Load(RequestDetail request)
    {
        _rowVersion = request.RowVersion;
        PatientOption = new RecordOption(request.PatientId, request.PatientName, null);
        PatientName = request.PatientName;
        UnitsText = request.Units.ToString(CultureInfo.InvariantCulture);
        RequiredBy = request.RequiredBy;
        _baselineUnits = request.Units;
        _baselineRequiredBy = request.RequiredBy;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    partial void OnPatientOptionChanged(RecordOption? value) => FieldEdited(nameof(PatientId));

    partial void OnUnitsTextChanged(string value) => FieldEdited(nameof(UnitsText));

    partial void OnRequiredByChanged(DateOnly? value) => FieldEdited(nameof(RequiredBy));

    protected override string? MapServerField(string serverField) => serverField.ToLowerInvariant() switch
    {
        "patientid" => nameof(PatientId),
        "units" => nameof(UnitsText),
        "requiredby" => nameof(RequiredBy),
        _ => null,
    };

    protected override string? ValidateFormField(string field)
    {
        switch (field)
        {
            case nameof(PatientId):
                return IsNew && PatientId is null ? "Choose a patient." : null;
            case nameof(UnitsText):
                var text = UnitsText.Trim();
                if (text.Length == 0)
                {
                    return $"Enter how many units are needed (1 to {MaxUnits}).";
                }

                return Units is { } units && units is >= 1 and <= MaxUnits ? null : $"Units must be a whole number from 1 to {MaxUnits}.";
            case nameof(RequiredBy):
                if (RequiredBy is not { } date)
                {
                    return "Choose the date the blood is needed by.";
                }

                if (!IsNew && date == _baselineRequiredBy)
                {
                    return null; // an unchanged date is not checked again: an overdue request can still have its units edited
                }

                if (date < LocalToday)
                {
                    return "The date can't be in the past. Choose today or a later date.";
                }

                return date > LatestRequiredBy ? $"The date can't be more than a year away (latest {Formatting.Date(LatestRequiredBy)})." : null;
            default:
                return null;
        }
    }

    /// <summary>The PATCH body of an edit: the row version and only what changed.</summary>
    public UpdateRequestRequest BuildUpdate() =>
        new(
            _rowVersion,
            Units == _baselineUnits ? default : Optional.Of(Units!.Value),
            RequiredBy == _baselineRequiredBy ? default : Optional.Of(RequiredBy!.Value));

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareSubmit()
    {
        if (_id is { } id)
        {
            var update = BuildUpdate();
            return async ct =>
            {
                var result = await _api.UpdateRequestAsync(id, update, ct).ConfigureAwait(false);
                return Finish(result);
            };
        }

        var create = new CreateRequestRequest(PatientId!.Value, Units!.Value, RequiredBy!.Value);
        return async ct =>
        {
            var result = await _api.CreateRequestAsync(create, ct).ConfigureAwait(false);
            return Finish(result);
        };
    }

    private ApiProblem? Finish(ApiResult<RequestDetail> result)
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
            var result = await _api.GetRequestAsync(id, ct).ConfigureAwait(false);
            _reloaded = result.IsOk ? result.Value : null;
            return result.Problem;
        };
    }

    protected override void ApplyReloaded()
    {
        if (_reloaded is { } request)
        {
            Load(request);
        }
    }

    protected override void AcceptAsBaseline()
    {
        _baselineUnits = Units;
        _baselineRequiredBy = RequiredBy;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }
}
