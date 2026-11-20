using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Contracts;
using Lifestream.Domain;

namespace Lifestream.Client.Features.ListScreens;

/// <summary>
/// Passed to a list screen when another screen opens it already filtered (a dashboard card, FEAT-30). Every member is optional.
/// <see cref="Status"/> is for screens that have a status filter (appointments, requests); <see cref="StockView"/> is for the stock screen.
/// <see cref="RecordId"/> opens that exact record: it is selected in the list when it is on the page and its detail is shown either way (a link from
/// another screen names the record by id, never by a name search, so two people with one name can't be mixed up, FEAT-14).
/// </summary>
public sealed record ListPrefilter(BloodType? BloodType = null, string? Search = null, string? Status = null, StockFilter? StockView = null, int? RecordId = null);

/// <summary>Passed to another screen to start an action on a record (for example "book an appointment for this donor").</summary>
public sealed record RelatedRecord(RecordKind Kind, int Id, string Name);

/// <summary>The stock screen's "show" filter.</summary>
public enum StockFilter
{
    All,
    BelowThreshold,
    ExpiringSoon,
}

/// <summary>
/// A list with a detail pane on the same screen (FEAT-04): selecting a row shows that record's detail, and create, edit and delete act on
/// the selected record. The editor opens in a side drawer. Delete asks first, names the record, changes the list only after the API
/// confirms, shows a plain message on failure and offers Undo after success (FEAT-06).
/// </summary>
public abstract partial class RecordListViewModel<TItem, TDetail> : PagedListViewModel<TItem>, INavigationParameterReceiver, IHasUnsavedChanges, IScrollMemory
    where TItem : class
    where TDetail : class
{
    private CancellationTokenSource? _detailCts;
    private int? _selectedId;
    private int? _openedId; // a record another screen asked to open by id: kept selected while it is not on the loaded page
    private bool _applying;
    private FilterOption<BloodType> _bloodTypeFilter;

    protected RecordListViewModel(IListViewStateStore states, string stateKey, IToastService toasts, IDialogService dialogs, IUiDispatcher ui)
        : base(states, stateKey, ui)
    {
        Toasts = toasts;
        Dialogs = dialogs;
        BloodTypeOptions = SharedLists.WithAll(SharedLists.BloodTypes, "All blood types");
        _bloodTypeFilter = BloodTypeOptions[0];
        if (ViewState.Filters.TryGetValue(BloodTypeKey, out var saved) && BloodTypeExtensions.TryParseDisplay(saved, out var remembered))
        {
            _bloodTypeFilter = BloodTypeOptions.First(o => o.Value == remembered);
        }

        AddCommand = Command(_ => AddAsync());
        EditCommand = Command(_ =>
        {
            OpenEditor(Detail);
            return Task.CompletedTask;
        }, () => Detail is not null && !IsEditing);
        DeleteCommand = Command(_ => DeleteSelectedAsync(), () => Detail is { } record && !IsEditing && CanDelete(record) && DeleteBlockOf(record) is null, opensModal: true);
        RetryDetailCommand = Command(_ => LoadDetailAsync(_selectedId));
        CancelDetailCommand = Command(_ =>
        {
            _detailCts?.Cancel();
            return Task.CompletedTask;
        });
        CancelWakingCommand = new AsyncRelayCommand(_ =>
        {
            _detailCts?.Cancel();
            Operations.Cancel();
            return Task.CompletedTask;
        });
    }

    /// <summary>The list's scroll position, kept in the session's <see cref="ListViewState"/> (SHELL-24).</summary>
    double IScrollMemory.ScrollOffset
    {
        get => ViewState.ScrollOffset;
        set => ViewState.ScrollOffset = value;
    }

    /// <summary>For the editors this screen opens.</summary>
    protected IToastService Toasts { get; }

    protected override IToastService? SessionToasts => Toasts;

    protected IDialogService Dialogs { get; }

    private const string BloodTypeKey = "bloodType";

    public IReadOnlyList<FilterOption<BloodType>> BloodTypeOptions { get; }

    /// <summary>The blood type filter; "All blood types" sends no filter. It applies immediately (FEAT-02).</summary>
    public FilterOption<BloodType> BloodTypeFilter
    {
        get => _bloodTypeFilter;
        set
        {
            if (SetProperty(ref _bloodTypeFilter, value))
            {
                ViewState.Filters[BloodTypeKey] = value.Value?.ToDisplay();
                FilterChanged();
            }
        }
    }

    protected BloodType? BloodTypeValue => _bloodTypeFilter.Value;

    protected override bool HasExtraFilters => _bloodTypeFilter.Value is not null;

    protected override void ResetExtraFilters()
    {
        _bloodTypeFilter = BloodTypeOptions[0];
        OnPropertyChanged(nameof(BloodTypeFilter));
    }

    public AsyncRelayCommand AddCommand { get; }

    public AsyncRelayCommand EditCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public AsyncRelayCommand RetryDetailCommand { get; }

    /// <summary>The "Cancel" of the detail pane's waking state: stops the detail request itself and offers Retry (SHELL-07).</summary>
    public AsyncRelayCommand CancelDetailCommand { get; }

    /// <summary>
    /// The "Cancel" of the waking notice of the screen's detail pane: stops whatever is waiting on the server, the detail request or any other operation of
    /// the screen (Delete, Undo, Cancel appointment, Cancel request, opening a linked record), down to the HTTP request (SHELL-07).
    /// </summary>
    [NoOperationRunner("It only cancels the screen's operations; it makes no API call.")]
    public AsyncRelayCommand CancelWakingCommand { get; }

    /// <summary>True while the detail request or any other operation of this screen is waiting on a waking server: the pane shows "Waking up the server" with Cancel (SHELL-07).</summary>
    public bool ShowWakingNotice => IsDetailWaking || IsOperationWaking;

    /// <summary>Ctrl+N opens the new-record drawer.</summary>
    public override AsyncRelayCommand? NewCommand => AddCommand;

    protected override string? NewActionText => NewActionLabel;

    /// <summary>The primary action names what it does: "Add donor".</summary>
    public string NewActionLabel => $"Add {RecordType}";

    /// <summary>The record kind in lower case, used in messages ("donor").</summary>
    protected abstract string RecordType { get; }

    /// <summary>The selected row. Selecting loads that record's detail; selecting nothing clears the pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    public partial TItem? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(ShowDetailSkeleton), nameof(HasDetailError), nameof(DetailTitle), nameof(IsDetailOpen), nameof(DeleteBlockedReason), nameof(ShowNoSelection))]
    public partial TDetail? Detail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(ShowDetailSkeleton), nameof(HasDetailError), nameof(IsDetailWaking), nameof(ShowWakingNotice), nameof(IsDetailOpen), nameof(ShowNoSelection))]
    public partial LoadState DetailState { get; set; } = LoadState.Ready;

    /// <summary>
    /// True while the detail pane has nothing to show (no record, no skeleton, no error): only then does it say "Select a donor to see their details." The pane can
    /// show a record that has no row in the list (it left the list's view when it was fulfilled or booked for another day, or another screen opened it by id), so
    /// the selected row does not decide this.
    /// </summary>
    public bool ShowNoSelection => Detail is null && DetailState == LoadState.Ready;

    /// <summary>
    /// True while the side panel has something to show: a selected record (loading, loaded or failed) or an open editor. Selecting nothing and closing the editor closes it
    /// (direction A, list and side panel).
    /// </summary>
    public bool IsDetailOpen => SelectedItem is not null || Detail is not null || DetailState != LoadState.Ready || Editor is not null;

    /// <summary>
    /// The side panel's close button and Esc: an open editor is cancelled first (it asks before discarding changes); otherwise the selection is cleared and the panel
    /// closes. It makes no API call of its own.
    /// </summary>
    public System.Windows.Input.ICommand CloseDetailCommand => _closeDetailCommand ??= new CommunityToolkit.Mvvm.Input.RelayCommand(CloseDetail);

    private System.Windows.Input.ICommand? _closeDetailCommand;

    private void CloseDetail()
    {
        if (Editor is { } editor)
        {
            if (editor.CancelCommand.CanExecute(null))
            {
                editor.CancelCommand.Execute(null);
            }

            return;
        }

        _openedId = null;
        _selectedId = null;
        if (SelectedItem is null)
        {
            ClearDetail();
        }
        else
        {
            SelectedItem = null;
        }
    }

    /// <summary>True while the detail request is waiting on a waking server (after 3 s): the pane shows "Waking up the server" with Cancel (SHELL-07).</summary>
    public bool IsDetailWaking => DetailState == LoadState.Waking;

    [ObservableProperty]
    public partial string? DetailError { get; set; }

    /// <summary>The label and value lines of the detail pane.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DetailFact> DetailFacts { get; set; } = [];

    /// <summary>The open editor (the drawer), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsDetailOpen))]
    public partial RecordEditorViewModel? Editor { get; set; }

    public bool IsEditing => Editor is not null;

    /// <summary>True while the open editor has unsaved changes: leaving the screen then asks "Discard changes?" (SHELL-24).</summary>
    public bool HasUnsavedChanges => Editor?.HasUnsavedChanges == true;

    public bool HasDetail => Detail is not null && DetailState == LoadState.Ready;

    public bool ShowDetailSkeleton => DetailState is LoadState.Loading or LoadState.Waking;

    public bool HasDetailError => DetailState == LoadState.Error;

    public string DetailTitle => Detail is { } d ? NameOfDetail(d) : $"No {RecordType} selected";

    public string NoSelectionText => $"Select {Article(RecordType)} {RecordType} to see {(RecordIsPerson ? "their" : "its")} details.";

    /// <summary>True when the record is a person (donor, patient, staff member): their details. Other records (an appointment, a request, a receipt) have its details.</summary>
    protected virtual bool RecordIsPerson => false;

    private static string Article(string noun) => noun.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(noun[0]), StringComparison.Ordinal) ? "an" : "a";

    protected abstract int IdOf(TItem item);

    protected abstract int IdOfDetail(TDetail detail);

    protected abstract string NameOfDetail(TDetail detail);

    protected abstract string NameOf(TItem item);

    [RunsInOperation("LoadDetailAsync calls it inside RunOperationAsync.")]
    protected abstract Task<ApiResult<TDetail>> GetDetailAsync(int id, CancellationToken ct);

    /// <summary>Extra detail lines that need their own call (a donor's eligibility). Failures must be returned as a line, never thrown.</summary>
    [RunsInOperation("LoadDetailAsync calls it inside RunOperationAsync.")]
    protected virtual Task<IReadOnlyList<DetailFact>> GetExtraFactsAsync(int id, CancellationToken ct) => Task.FromResult<IReadOnlyList<DetailFact>>([]);

    protected abstract IReadOnlyList<DetailFact> FactsOf(TDetail detail);

    [RunsInOperation("DeleteSelectedAsync calls it inside RunOperationAsync.")]
    protected abstract Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct);

    /// <summary>Puts a deleted record back (Undo). The API restores it with the same id and every value, so nothing the list does not show (a last-donation date) is lost.</summary>
    [RunsInOperation("UndoAsync calls it inside the toast action's shell-level operation (ToastService.Actions).")]
    protected abstract Task<ApiResult<TDetail>> RestoreAsync(TDetail deleted, CancellationToken ct);

    /// <summary>Whether the selected record may be deleted at all (the signed-in admin's own staff record may not).</summary>
    protected virtual bool CanDelete(TDetail detail) => true;

    /// <summary>
    /// Why the server will refuse to delete this record (other records point at it: a donor's appointments, a patient's requests), or null when it can be deleted or
    /// the screen does not know. Delete is not offered while there is a reason; the "..." menu shows the reason under the disabled item.
    /// </summary>
    protected virtual string? DeleteBlockOf(TDetail detail) => null;

    /// <summary>Why the selected record can't be deleted (<see cref="DeleteBlockOf"/>), or null: shown with the disabled Delete.</summary>
    public string? DeleteBlockedReason => Detail is { } record ? DeleteBlockOf(record) : null;

    /// <summary>
    /// What to say when the server refuses a delete because other records point at the record (409 conflict): the screen could not know beforehand (the count of
    /// those records could not be read), or it changed since. It says "deleted", why, and that nothing was changed. Null (the default) for a screen whose records
    /// have no such rule (a receipt, a request): its refusals keep the general message, and nothing is read again because of them.
    /// </summary>
    protected virtual string? DeleteRefusedText(string name) => null;

    /// <summary>Call when what <see cref="DeleteBlockOf"/> answers may have changed (the record's history has loaded).</summary>
    protected void RaiseDeleteBlockChanged()
    {
        OnPropertyChanged(nameof(DeleteBlockedReason));
        DeleteCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Whether a deleted record can be offered back with Undo; false for records the API cannot restore (staff).</summary>
    protected virtual bool SupportsUndo => true;

    protected abstract RecordEditorViewModel CreateEditor(TDetail? existing);

    /// <summary>Applies what another screen asked for (a dashboard card) before the first load.</summary>
    protected virtual void ApplyPrefilter(ListPrefilter prefilter)
    {
        if (prefilter.BloodType is { } type)
        {
            _bloodTypeFilter = BloodTypeOptions.First(o => o.Value == type);
            ViewState.Filters[BloodTypeKey] = type.ToDisplay();
            OnPropertyChanged(nameof(BloodTypeFilter));
        }

        if (prefilter.Search is { } search)
        {
            SetSearchWithoutRefresh(search);
        }

        if (prefilter.RecordId is { } recordId)
        {
            _selectedId = recordId;
            _openedId = recordId;
            PendingDetail = LoadDetailAsync(recordId); // the detail does not wait for the list, and does not depend on the record being on its first page
        }

        OnFiltersChanged();
    }

    public void ReceiveParameter(object parameter)
    {
        if (parameter is ListPrefilter prefilter)
        {
            ApplyPrefilter(prefilter);
        }
    }

    protected override void Apply()
    {
        _applying = true;
        try
        {
            base.Apply();
            // Keep the same record selected across a refresh (a page change, a filter, a demo reset).
            var keep = _selectedId is { } id ? Items.FirstOrDefault(i => IdOf(i) == id) : null;
            SelectedItem = keep;
            if (keep is null && _selectedId is not null && _selectedId != _openedId)
            {
                ClearDetail();
            }
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnSelectedItemChanged(TItem? value)
    {
        if (_applying)
        {
            return;
        }

        _selectedId = value is null ? null : IdOf(value);
        _openedId = null; // the user chose something themselves
        if (value is null)
        {
            ClearDetail();
            return;
        }

        PendingDetail = LoadDetailAsync(_selectedId);
    }

    /// <summary>Completes when the detail load started by the last selection has finished (for tests).</summary>
    internal Task PendingDetail { get; private set; } = Task.CompletedTask;

    partial void OnDetailChanged(TDetail? value)
    {
        EditCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    // The extra lines arrive with the record's history (a donor's appointments): whether it can be deleted is known from then on.
    partial void OnDetailFactsChanged(IReadOnlyList<DetailFact> value) => RaiseDeleteBlockChanged();

    partial void OnEditorChanged(RecordEditorViewModel? value)
    {
        EditCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    private void ClearDetail()
    {
        _detailWakingTimer?.Dispose();
        _detailWakingTimer = null;
        _detailCts?.Cancel();
        _detailCts = null;
        Detail = null;
        DetailFacts = [];
        DetailError = null;
        DetailState = LoadState.Ready;
    }

    /// <summary>Loads the detail of the record with this id (one call, plus the extra lines some records have).</summary>
    public async Task LoadDetailAsync(int? id)
    {
        _detailCts?.Cancel();
        if (id is not { } recordId)
        {
            ClearDetail();
            return;
        }

        var cts = new CancellationTokenSource();
        _detailCts = cts;
        DetailError = null;
        DetailState = LoadState.Loading;
        WatchDetailWaking(cts);
        try
        {
            // Each load keeps what it fetched to itself: a load replaced by a newer selection may still finish its call afterwards, and must never hand its
            // record to the newer load (the panel would show another record than the selected row).
            var (fetched, extraFacts) = await RunOperationAsync(async ct =>
            {
                var result = await GetDetailAsync(recordId, ct).ConfigureAwait(false);
                var record = result.Unwrap();
                return (record, await GetExtraFactsAsync(recordId, ct).ConfigureAwait(false));
            }, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || fetched is not { } detail)
            {
                return;
            }

            Detail = detail;
            DetailFacts = [.. FactsOf(detail), .. extraFacts];
            DetailState = LoadState.Ready;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_detailCts, cts))
            {
                // Not replaced by a newer selection and not cleared: the user cancelled the request. Say so and offer Retry.
                Detail = null;
                DetailFacts = [];
                DetailError = "The request was cancelled.";
                DetailState = LoadState.Error;
            }
        }
        catch (ApiProblemException ex) when (!cts.IsCancellationRequested)
        {
            Detail = null;
            DetailFacts = [];
            DetailError = ProblemText.For(ex.Problem, RecordType);
            DetailState = LoadState.Error;
        }
        catch (ApiProblemException)
        {
            // Replaced by a newer selection.
        }
    }

    private ITimer? _detailWakingTimer;

    /// <summary>The detail pane shows the waking state after the same 3 s as every operation, also when the server is already waking for another call (SHELL-07).</summary>
    private void WatchDetailWaking(CancellationTokenSource cts)
    {
        _detailWakingTimer?.Dispose();
        _detailWakingTimer = null;
        if (!Operations.IsWaking && CurrentServerStatus?.State != ServerState.Waking)
        {
            return;
        }

        _detailWakingTimer = Clock.CreateTimer(
            _ => Post(() =>
            {
                if (ReferenceEquals(_detailCts, cts) && DetailState == LoadState.Loading && CurrentServerStatus?.State == ServerState.Waking)
                {
                    DetailState = LoadState.Waking;
                }
            }),
            null,
            WakingAfter,
            Timeout.InfiniteTimeSpan);
    }

    protected override void OnOperationWakingChanged(bool isWaking)
    {
        base.OnOperationWakingChanged(isWaking);
        OnPropertyChanged(nameof(ShowWakingNotice));
        if (isWaking && DetailState == LoadState.Loading && _detailCts is not null)
        {
            DetailState = LoadState.Waking;
        }
        else if (!isWaking && DetailState == LoadState.Waking)
        {
            DetailState = LoadState.Loading;
        }
    }

    /// <summary>"Add" and Ctrl+N: a new record replaces the open editor, so a dirty one asks "Discard changes?" first (SHELL-24).</summary>
    private async Task AddAsync()
    {
        if (HasUnsavedChanges
            && !await Dialogs.ConfirmAsync("Discard changes?", $"You have unsaved changes to this {RecordType}. If you start a new one now they will be lost.", "Discard changes", "Keep editing").ConfigureAwait(true))
        {
            return;
        }

        OpenEditor(null);
    }

    private void OpenEditor(TDetail? existing)
    {
        if (Editor is { } previous)
        {
            previous.Closed -= OnEditorClosed;
            previous.Dispose();
        }

        var editor = CreateEditor(existing);
        AttachChild(editor); // its save and reload show the waking state and can be cancelled like every operation (SHELL-07)
        editor.Closed += OnEditorClosed;
        Editor = editor;
    }

    private void OnEditorClosed(object? sender, EditorClosedEventArgs e)
    {
        if (sender is RecordEditorViewModel editor)
        {
            editor.Closed -= OnEditorClosed;
            editor.Dispose();
        }

        Editor = null;
        if (e.Saved)
        {
            if (e.Record is TDetail saved)
            {
                _selectedId = IdOfDetail(saved);
            }

            _ = RefreshAfterSaveAsync();
        }
    }

    private async Task RefreshAfterSaveAsync()
    {
        await RefreshAsync().ConfigureAwait(true);
        if (_selectedId is { } id)
        {
            await LoadDetailAsync(id).ConfigureAwait(true);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (Detail is not { } record)
        {
            return;
        }

        var name = NameOfDetail(record);
        var confirmed = await Dialogs.ConfirmAsync(
            $"Delete {RecordType}?",
            SupportsUndo
                ? $"{name} will be removed from the list. You can undo this for a few seconds afterwards."
                : $"{name} will be removed. This can't be undone.",
            $"Delete {RecordType}",
            $"Keep {RecordType}").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var id = IdOfDetail(record);
        ApiResult result = null!;
        await RunBusyAsync(async () => result = await RunOperationAsync(ct => DeleteRecordAsync(id, ct)).ConfigureAwait(true)).ConfigureAwait(true);
        if (!result.IsOk)
        {
            // The list stays exactly as it was; only a message says why.
            if (result.Problem!.Code == ProblemCodes.Conflict && DeleteRefusedText(name) is { } refused)
            {
                // Other records point at it (the screen did not know, or it changed since): say so, and read the record and its counts again, so that Delete is
                // offered or not by what the server says now.
                Toasts.Show(ToastKind.Error, refused);
                await LoadDetailAsync(id).ConfigureAwait(true);
                return;
            }

            Toasts.Show(ToastKind.Error, ProblemText.For(result.Problem!, RecordType, name));
            return;
        }

        _selectedId = null;
        SelectedItem = null;
        ClearDetail();
        await RefreshAsync().ConfigureAwait(true);
        var deletedAt = Clock.GetUtcNow();
        var clock = Clock;
        Toasts.ShowForRecord(ToastKind.Success, Capitalise(RecordType), name, "deleted", SupportsUndo ? "Undo" : null, SupportsUndo ? () => UndoAsync(record, deletedAt, clock) : null);
    }

    /// <summary>
    /// Runs the restore. When it is cancelled or fails (waking server, no connection, timeout) while the server's restore window (<see cref="UndoWindow"/>) is
    /// still open, a toast says the record is still deleted and offers "Retry undo", which runs this again with the same waking notice and Cancel. When the
    /// window has closed, or the server refuses because it has, the toast says the record can no longer be restored and has no action (SHELL-07).
    /// </summary>
    private async Task UndoAsync(TDetail deleted, DateTimeOffset deletedAt, TimeProvider clock)
    {
        var name = NameOfDetail(deleted);
        bool WindowOpen() => clock.GetUtcNow() - deletedAt < UndoWindow.Duration;
        void Failed(string reason)
        {
            if (!WindowOpen())
            {
                Toasts.Show(ToastKind.Error, $"Couldn't restore {RecordType} '{name}'. It can no longer be restored.");
                return;
            }

            var remaining = UndoWindow.Duration - (clock.GetUtcNow() - deletedAt);
            Toasts.Show(ToastKind.Warning, $"{Capitalise(RecordType)} '{name}' is still deleted.{(reason.Length > 0 ? " " + reason : string.Empty)}", "Retry undo", () => UndoAsync(deleted, deletedAt, clock), remaining);
        }

        if (!WindowOpen())
        {
            Failed(string.Empty); // the user waited too long before pressing Undo or Retry: nothing to ask the server
            return;
        }

        ApiResult<TDetail> result;
        try
        {
            // Runs inside the toast's own (shell-level) operation, not this screen's: the toast outlives the screen, so after the user has gone elsewhere the
            // waking notice and its Cancel are the shell's, and this view model (disposed by then) is not needed for them (SHELL-07).
            result = await RestoreAsync(deleted, OperationScope.Current?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Failed("The restore was cancelled.");
            return;
        }

        if (!result.IsOk)
        {
            var problem = result.Problem!;
            if (problem.Status is 404 or 409 || problem.Code == ProblemCodes.DemoForbidden)
            {
                // The server answered: a 404 means its window has closed (or the record is gone), the others will not change on a retry.
                Toasts.Show(ToastKind.Error, $"Couldn't restore {RecordType} '{name}'. {(problem.Status == 404 ? "It can no longer be restored." : ProblemText.For(problem, RecordType, name))}");
                return;
            }

            Failed(ProblemText.For(problem, RecordType, name));
            return;
        }

        Toasts.ShowForRecord(ToastKind.Success, Capitalise(RecordType), name, "restored");
        if (IsDisposed)
        {
            return; // the user left the screen; the next visit loads the list again
        }

        _selectedId = IdOfDetail(result.Value);
        await RefreshAfterSaveAsync().ConfigureAwait(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _detailWakingTimer?.Dispose();
            var detail = _detailCts;
            _detailCts = null;
            detail?.Cancel();
            detail?.Dispose();
            Editor?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
