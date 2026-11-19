using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
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
public abstract partial class RecordListViewModel<TItem, TDetail> : PagedListViewModel<TItem>, INavigationParameterReceiver, IDirtyTracking, IScrollMemory
    where TItem : class
    where TDetail : class
{
    private CancellationTokenSource? _detailCts;
    private int? _selectedId;
    private int? _openedId; // a record another screen asked to open by id: kept selected while it is not on the loaded page
    private bool _applying;
    private TDetail? _fetchedDetail;
    private IReadOnlyList<DetailFact> _fetchedExtraFacts = [];
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

        AddCommand = new AsyncRelayCommand(_ => AddAsync());
        EditCommand = new AsyncRelayCommand(_ =>
        {
            OpenEditor(Detail);
            return Task.CompletedTask;
        }, () => Detail is not null && !IsEditing);
        DeleteCommand = new AsyncRelayCommand(_ => DeleteSelectedAsync(), () => Detail is { } record && !IsEditing && CanDelete(record));
        RetryDetailCommand = new AsyncRelayCommand(_ => LoadDetailAsync(_selectedId));
    }

    /// <summary>The list's scroll position, kept in the session's <see cref="ListViewState"/> (SHELL-24).</summary>
    double IScrollMemory.ScrollOffset
    {
        get => ViewState.ScrollOffset;
        set => ViewState.ScrollOffset = value;
    }

    /// <summary>For the editors this screen opens.</summary>
    protected IToastService Toasts { get; }

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

    /// <summary>Ctrl+N opens the new-record drawer.</summary>
    public override AsyncRelayCommand? NewCommand => AddCommand;

    protected override string? NewActionText => NewActionLabel;

    /// <summary>The primary action names what it does: "Add donor".</summary>
    public string NewActionLabel => $"Add {RecordType}";

    /// <summary>The record kind in lower case, used in messages ("donor").</summary>
    protected abstract string RecordType { get; }

    /// <summary>The selected row. Selecting loads that record's detail; selecting nothing clears the pane.</summary>
    [ObservableProperty]
    public partial TItem? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(ShowDetailSkeleton), nameof(HasDetailError), nameof(DetailTitle))]
    public partial TDetail? Detail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(ShowDetailSkeleton), nameof(HasDetailError))]
    public partial LoadState DetailState { get; set; } = LoadState.Ready;

    [ObservableProperty]
    public partial string? DetailError { get; set; }

    /// <summary>The label and value lines of the detail pane.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DetailFact> DetailFacts { get; set; } = [];

    /// <summary>The open editor (the drawer), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial RecordEditorViewModel? Editor { get; set; }

    public bool IsEditing => Editor is not null;

    /// <summary>True while the open editor has unsaved changes: leaving the screen then asks "Discard changes?" (SHELL-24).</summary>
    public bool HasUnsavedChanges => Editor?.HasUnsavedChanges == true;

    public bool HasDetail => Detail is not null && DetailState == LoadState.Ready;

    public bool ShowDetailSkeleton => DetailState is LoadState.Loading or LoadState.Waking;

    public bool HasDetailError => DetailState == LoadState.Error;

    public string DetailTitle => Detail is { } d ? NameOfDetail(d) : $"No {RecordType} selected";

    public string NoSelectionText => $"Select a {RecordType} to see their details.";

    protected abstract int IdOf(TItem item);

    protected abstract int IdOfDetail(TDetail detail);

    protected abstract string NameOfDetail(TDetail detail);

    protected abstract string NameOf(TItem item);

    protected abstract Task<ApiResult<TDetail>> GetDetailAsync(int id, CancellationToken ct);

    /// <summary>Extra detail lines that need their own call (a donor's eligibility). Failures must be returned as a line, never thrown.</summary>
    protected virtual Task<IReadOnlyList<DetailFact>> GetExtraFactsAsync(int id, CancellationToken ct) => Task.FromResult<IReadOnlyList<DetailFact>>([]);

    protected abstract IReadOnlyList<DetailFact> FactsOf(TDetail detail);

    protected abstract Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct);

    /// <summary>Puts a deleted record back (Undo). The API restores it with the same id and every value, so nothing the list does not show (a last-donation date) is lost.</summary>
    protected abstract Task<ApiResult<TDetail>> RestoreAsync(TDetail deleted, CancellationToken ct);

    /// <summary>Whether the selected record may be deleted at all (the signed-in admin's own staff record may not).</summary>
    protected virtual bool CanDelete(TDetail detail) => true;

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

    partial void OnEditorChanged(RecordEditorViewModel? value)
    {
        EditCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    private void ClearDetail()
    {
        _detailCts?.Cancel();
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
        try
        {
            await Task.Run(async () =>
            {
                var result = await GetDetailAsync(recordId, cts.Token).ConfigureAwait(false);
                _fetchedDetail = result.Unwrap();
                _fetchedExtraFacts = await GetExtraFactsAsync(recordId, cts.Token).ConfigureAwait(false);
            }, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || _fetchedDetail is not { } detail)
            {
                return;
            }

            Detail = detail;
            DetailFacts = [.. FactsOf(detail), .. _fetchedExtraFacts];
            DetailState = LoadState.Ready;
        }
        catch (OperationCanceledException)
        {
            // A newer selection replaced this one.
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

    /// <summary>"Add" and Ctrl+N: a new record replaces the open editor, so a dirty one asks "Discard changes?" first (SHELL-24).</summary>
    private async Task AddAsync()
    {
        if (HasUnsavedChanges
            && !await Dialogs.ConfirmAsync("Discard changes?", $"You have unsaved changes to this {RecordType}. If you start a new one now they will be lost.", "Discard changes").ConfigureAwait(true))
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
                ? $"{Capitalise(RecordType)} '{name}' will be removed from the list. You can undo this for a few seconds afterwards."
                : $"{Capitalise(RecordType)} '{name}' will be removed. This can't be undone.",
            $"Delete {RecordType}").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var id = IdOfDetail(record);
        ApiResult result = null!;
        await RunBusyAsync(async () => result = await Task.Run(() => DeleteRecordAsync(id, CancellationToken.None)).ConfigureAwait(true)).ConfigureAwait(true);
        if (!result.IsOk)
        {
            // The list stays exactly as it was; only a message says why.
            Toasts.Show(ToastKind.Error, ProblemText.For(result.Problem!, RecordType, name));
            return;
        }

        _selectedId = null;
        SelectedItem = null;
        ClearDetail();
        await RefreshAsync().ConfigureAwait(true);
        Toasts.ShowForRecord(ToastKind.Success, Capitalise(RecordType), name, "deleted", SupportsUndo ? "Undo" : null, SupportsUndo ? () => UndoAsync(record) : null);
    }

    private async Task UndoAsync(TDetail deleted)
    {
        var name = NameOfDetail(deleted);
        var result = await Task.Run(() => RestoreAsync(deleted, CancellationToken.None)).ConfigureAwait(true);
        if (!result.IsOk)
        {
            Toasts.Show(ToastKind.Error, $"Couldn't restore {RecordType} '{name}'. {ProblemText.For(result.Problem!, RecordType, name)}");
            return;
        }

        _selectedId = IdOfDetail(result.Value);
        Toasts.ShowForRecord(ToastKind.Success, Capitalise(RecordType), name, "restored");
        await RefreshAfterSaveAsync().ConfigureAwait(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
