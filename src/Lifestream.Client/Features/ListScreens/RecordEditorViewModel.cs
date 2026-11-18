using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Validation;
using Lifestream.Contracts;

namespace Lifestream.Client.Features.ListScreens;

/// <summary>How an editor ended: saved (with the record the API returned) or cancelled.</summary>
public sealed class EditorClosedEventArgs(bool saved, object? record) : EventArgs
{
    public bool Saved { get; } = saved;

    public object? Record { get; } = record;
}

/// <summary>
/// Base of the create and edit forms (FEAT-05, FEAT-07, FEAT-08). Required fields are validated before anything is sent, the API's
/// field errors are shown under the fields, a second save while one runs is ignored (the button is busy), a 409 shows a "reload"
/// prompt and never overwrites, and Cancel is always available and asks "Discard changes?" only when the form is dirty.
/// </summary>
public abstract partial class RecordEditorViewModel : FormViewModelBase
{
    private readonly IToastService _toasts;
    private readonly IDialogService _dialogs;

    protected RecordEditorViewModel(IToastService toasts, IDialogService dialogs, TimeProvider clock, string recordType, bool isNew)
        : base()
    {
        State = LoadState.Ready;
        _toasts = toasts;
        _dialogs = dialogs;
        Today = clock;
        RecordType = recordType;
        IsNew = isNew;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new AsyncRelayCommand(CancelAsync);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync);
        ErrorsChanged += (_, _) => OnPropertyChanged("Item[]"); // lets the view bind {Binding [Name]} to a field's message
    }

    /// <summary>The message of a field, or null; bound by the view as <c>{Binding [Name]}</c> under each field.</summary>
    public string? this[string field] => GetError(field);

    /// <summary>Raised once when the form is saved or cancelled; the screen then closes the drawer.</summary>
    public event EventHandler<EditorClosedEventArgs>? Closed;

    public string RecordType { get; }

    public bool IsNew { get; }

    private TimeProvider Today { get; }

    /// <summary>The local date, from the app clock.</summary>
    protected DateOnly LocalToday => DateOnly.FromDateTime(Today.GetLocalNow().DateTime);

    public string Title => IsNew ? $"New {RecordType}" : $"Edit {RecordType}";

    /// <summary>The primary action names what it does (design system), not just "Save".</summary>
    public string SaveText => IsNew ? $"Add {RecordType}" : "Save changes";

    public string SaveBusyText => IsNew ? "Adding..." : "Saving...";

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>A form-wide message that belongs to no single field (a failed save, a record that vanished).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    public partial string? FormError { get; set; }

    public bool HasFormError => !string.IsNullOrEmpty(FormError);

    /// <summary>True after a 409: someone else changed the record. The form offers Reload and never overwrites.</summary>
    [ObservableProperty]
    public partial bool HasConflict { get; set; }

    public string ConflictMessage => $"This {RecordType} was changed by someone else while you were editing. Reload to see the latest version, then make your change again.";

    /// <summary>The name shown in toasts ("Donor 'Name' saved.").</summary>
    protected abstract string RecordName { get; }

    /// <summary>Called on the UI thread: reads the fields, builds the request, and returns the work that sends it (null means success; the saved record goes to <see cref="SavedRecord"/>).</summary>
    protected abstract Func<CancellationToken, Task<ApiProblem?>> PrepareSubmit();

    /// <summary>Called on the UI thread: returns the work that fetches the latest version (null means success; <see cref="ApplyReloaded"/> then runs on the UI thread).</summary>
    protected abstract Func<CancellationToken, Task<ApiProblem?>> PrepareReload();

    protected abstract void ApplyReloaded();

    /// <summary>Marks the current field values as the saved baseline (after a save or a reload).</summary>
    protected abstract void AcceptAsBaseline();

    /// <summary>Maps an API field name (any casing) to the form's field name, or null when the form has no such field.</summary>
    protected virtual string? MapServerField(string serverField) => DeclaredFieldNames.FirstOrDefault(f => string.Equals(f, serverField, StringComparison.OrdinalIgnoreCase));

    private readonly Dictionary<string, string> _serverErrors = [];
    private string[] _fieldNames = [];

    /// <summary>The fields in visual order.</summary>
    protected IReadOnlyList<string> DeclaredFieldNames => _fieldNames;

    /// <summary>Declares the fields in visual (tab) order, so focus goes to the first invalid one.</summary>
    protected void DeclareFormFields(params string[] fieldsInVisualOrder)
    {
        _fieldNames = fieldsInVisualOrder;
        DeclareFields(fieldsInVisualOrder);
    }

    /// <summary>Validates one field by the form's own rules (the API's messages are added by this base class).</summary>
    protected abstract string? ValidateFormField(string field);

    protected sealed override string? ValidateField(string field) => _serverErrors.GetValueOrDefault(field) ?? ValidateFormField(field);

    /// <summary>What the last successful save returned.</summary>
    protected object? SavedRecord { get; set; }

    private async Task SaveAsync(CancellationToken ct)
    {
        FormError = null;
        HasConflict = false;
        _serverErrors.Clear();
        if (!ValidateAll())
        {
            return;
        }

        var submit = PrepareSubmit();
        ApiProblem? problem = null;
        await RunBusyAsync(async () =>
        {
            try
            {
                problem = await Task.Run(() => submit(ct), ct).ConfigureAwait(true);
            }
            catch (ApiProblemException ex)
            {
                problem = ex.Problem;
            }
        }).ConfigureAwait(true);

        if (problem is null)
        {
            AcceptAsBaseline();
            _toasts.ShowForRecord(ToastKind.Success, Capitalise(RecordType), RecordName, IsNew ? "added" : "saved");
            Closed?.Invoke(this, new EditorClosedEventArgs(true, SavedRecord));
            return;
        }

        ShowFailure(problem);
    }

    private void ShowFailure(ApiProblem problem)
    {
        if (problem.Code == ProblemCodes.Concurrency)
        {
            HasConflict = true;
            return;
        }

        if (problem.Code == ProblemCodes.Validation)
        {
            var shown = 0;
            foreach (var (serverField, message) in ProblemFieldErrors.Read(problem))
            {
                if (MapServerField(serverField) is { } field)
                {
                    SetServerError(field, message);
                    shown++;
                }
            }

            if (shown > 0)
            {
                ValidateAll(); // shows the messages and moves focus to the first invalid field
                return;
            }
        }

        FormError = ProblemText.For(problem, RecordType, IsNew ? null : RecordName);
    }

    private async Task CancelAsync(CancellationToken ct)
    {
        if (SaveCommand.IsRunning)
        {
            return;
        }

        if (HasUnsavedChanges && !await _dialogs.ConfirmAsync("Discard changes?", $"You have unsaved changes to this {RecordType}. If you cancel now they will be lost.", "Discard changes").ConfigureAwait(true))
        {
            return;
        }

        Closed?.Invoke(this, new EditorClosedEventArgs(false, null));
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        var reload = PrepareReload();
        ApiProblem? problem = null;
        await RunBusyAsync(async () => problem = await Task.Run(() => reload(ct), ct).ConfigureAwait(true)).ConfigureAwait(true);
        if (problem is null)
        {
            ApplyReloaded();
            AcceptAsBaseline();
            HasConflict = false;
            FormError = null;
            return;
        }

        FormError = ProblemText.For(problem, RecordType, IsNew ? null : RecordName);
    }

    /// <summary>Remembers an API field message so the next validation shows it under its field until the user edits that field.</summary>
    public void SetServerError(string field, string message) => _serverErrors[field] = message;

    /// <summary>Call from a field's change handler: the field's message goes away the moment the user edits it, and the dirty state is re-read.</summary>
    protected void FieldEdited(string field)
    {
        _serverErrors.Remove(field);
        ClearError(field);
        FormError = null;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
