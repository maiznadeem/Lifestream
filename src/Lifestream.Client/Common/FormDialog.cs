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

/// <summary>
/// Opens form dialogs. Before the dialog is shown it is given the server status and clock (so its submit shows the waking state and can be
/// cancelled) and the dialog service (so closing it with unsaved input asks "Discard changes?").
/// </summary>
public sealed class FormDialogService(IServerStatus? serverStatus = null, IDialogService? dialogs = null, TimeProvider? clock = null) : IFormDialogService
{
    public IFormDialogPresenter? Presenter { get; set; }

    public async Task<bool> ShowAsync(DialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (Presenter is null)
        {
            return false;
        }

        viewModel.Bind(serverStatus, clock ?? TimeProvider.System, dialogs);
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
        PrimaryCommand = Command(RunPrimaryAsync, () => CanRunPrimary);
        CancelCommand = Command(CancelAsync);
        ErrorsChanged += (_, _) => OnPropertyChanged("Item[]"); // lets the view bind {Binding [Name]} to a field's message
    }

    /// <summary>Gives the dialog the app's server status, clock and dialog service (called by <see cref="FormDialogService"/> before it is shown).</summary>
    internal void Bind(IServerStatus? serverStatus, TimeProvider clock, IDialogService? dialogs)
    {
        Dialogs = dialogs;
        if (!_bound)
        {
            Attach(serverStatus, clock);
            _bound = true;
        }
    }

    private bool _bound;

    /// <summary>Asks "Discard changes?" when closing with unsaved input.</summary>
    internal IDialogService? Dialogs { get; private set; }

    /// <summary>True once the dialog has been told to close: the window then really closes (any earlier close, such as the X, is routed through <see cref="CancelCommand"/> first).</summary>
    public bool IsClosing { get; private set; }

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

    /// <summary>
    /// Every way out of the dialog (Esc, the Cancel button, the window's X) comes here. While the primary action is running it cancels the request
    /// (the HTTP call stops; the dialog stays open so nothing typed is lost). Otherwise a dialog with unsaved input asks "Discard changes?" and
    /// closes only when the user agrees (SHELL-24).
    /// </summary>
    private async Task CancelAsync(CancellationToken ct)
    {
        if (PrimaryCommand.IsRunning)
        {
            Operations.Cancel();
            return;
        }

        if (HasUnsavedChanges)
        {
            var dialogs = Dialogs ?? throw new InvalidOperationException("This dialog has unsaved changes but was not opened through the form dialog service, so it cannot ask \"Discard changes?\".");
            if (!await dialogs.ConfirmAsync("Discard changes?", "You have unsaved changes in this dialog. If you close it now they will be lost.", "Discard changes").ConfigureAwait(true))
            {
                return;
            }
        }

        Close();
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
        var cancelled = false;
        await RunBusyAsync(async () =>
        {
            try
            {
                problem = await RunOperationAsync(token => submit(token), ct).ConfigureAwait(true);
            }
            catch (ApiProblemException ex)
            {
                problem = ex.Problem;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
        }).ConfigureAwait(true);

        if (cancelled)
        {
            FormError = "The request was cancelled. What you entered is still here, so you can try again.";
            return;
        }

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
    protected void Close()
    {
        IsClosing = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
