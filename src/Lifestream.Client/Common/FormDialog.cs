using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Validation;

namespace Lifestream.Client.Common;

/// <summary>The view side of <see cref="IFormDialogService"/>; the Desktop project registers the WPF implementation (a modal window).</summary>
public interface IFormDialogPresenter
{
    /// <summary>Shows the dialog for the view model and completes when it has been closed.</summary>
    Task ShowAsync(DialogViewModel viewModel);
}

/// <summary>Opens a focused dialog (record a donation, fulfil a request, change a password, view a receipt) over the current screen.</summary>
public interface IFormDialogService
{
    /// <summary>Shows the dialog and returns true when it finished its action (<see cref="DialogViewModel.Completed"/>), false when it was cancelled or closed.</summary>
    Task<bool> ShowAsync(DialogViewModel viewModel);
}

public sealed class FormDialogService : IFormDialogService
{
    public IFormDialogPresenter? Presenter { get; set; }

    public async Task<bool> ShowAsync(DialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (Presenter is null)
        {
            return false;
        }

        await Presenter.ShowAsync(viewModel).ConfigureAwait(true);
        return viewModel.Completed;
    }
}

/// <summary>
/// Base of every dialog view model. It has its own validation (<see cref="FormViewModelBase"/>), a primary action that runs one API call
/// while its button is busy, a form-wide error message, and a Cancel (or Close) that is always available. A dialog never closes itself on a
/// failed call: the message stays in the dialog so nothing the user typed is lost.
/// </summary>
public abstract partial class DialogViewModel : FormViewModelBase
{
    protected DialogViewModel(string title, string? primaryText, string? primaryBusyText = null, string cancelText = "Cancel")
    {
        State = LoadState.Ready;
        Title = title;
        PrimaryText = primaryText;
        PrimaryBusyText = primaryBusyText ?? primaryText ?? string.Empty;
        CancelText = cancelText;
        PrimaryCommand = new AsyncRelayCommand(RunPrimaryAsync, () => CanRunPrimary);
        CancelCommand = new AsyncRelayCommand(_ =>
        {
            Close();
            return Task.CompletedTask;
        });
        ErrorsChanged += (_, _) => OnPropertyChanged("Item[]"); // lets the view bind {Binding [Name]} to a field's message
    }

    /// <summary>Raised once when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    public string Title { get; }

    /// <summary>The text of the primary action ("Record donation"), or null when the dialog has none (a viewer).</summary>
    public string? PrimaryText { get; }

    public string PrimaryBusyText { get; }

    public string CancelText { get; }

    public bool HasPrimary => PrimaryText is not null;

    public AsyncRelayCommand PrimaryCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    /// <summary>True once the primary action succeeded.</summary>
    public bool Completed { get; private set; }

    /// <summary>The message of a field, or null; bound by the view as <c>{Binding [Name]}</c> under each field.</summary>
    public string? this[string field] => GetError(field);

    /// <summary>A message that belongs to no single field (a failed call). Shown in the dialog, in a live region.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    public partial string? FormError { get; set; }

    public bool HasFormError => !string.IsNullOrEmpty(FormError);

    /// <summary>What kind of record this dialog is about, used in plain-language messages ("appointment").</summary>
    protected abstract string RecordType { get; }

    /// <summary>The primary button is enabled while this is true and nothing is running.</summary>
    protected virtual bool CanRunPrimary => true;

    /// <summary>Validates one field by the dialog's own rules.</summary>
    protected override string? ValidateField(string field) => null;

    /// <summary>Called on the UI thread: reads the fields, builds the request and returns the work that sends it (null result means success).</summary>
    protected virtual Func<CancellationToken, Task<ApiProblem?>>? PrepareSubmit() => null;

    /// <summary>Runs on the UI thread after a successful call, before the dialog closes.</summary>
    protected virtual void OnSucceeded()
    {
    }

    /// <summary>Turns a failed call into messages; the default shows one plain-language line.</summary>
    protected virtual void ShowProblem(ApiProblem problem) => FormError = ProblemText.For(problem, RecordType);

    /// <summary>Re-evaluates whether the primary button is enabled.</summary>
    protected void RaisePrimaryChanged() => PrimaryCommand.RaiseCanExecuteChanged();

    /// <summary>Call from a field's change handler: its message goes away when the user edits it.</summary>
    protected void FieldEdited(string field)
    {
        ClearError(field);
        FormError = null;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private async Task RunPrimaryAsync(CancellationToken ct)
    {
        FormError = null;
        if (!ValidateAll())
        {
            return;
        }

        var submit = PrepareSubmit();
        if (submit is null)
        {
            return;
        }

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
            Completed = true;
            OnSucceeded();
            Close();
            return;
        }

        ShowProblem(problem);
    }

    protected override void OnBusyChanged(bool isBusy) => RaisePrimaryChanged();

    /// <summary>Closes the dialog (once).</summary>
    protected void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
