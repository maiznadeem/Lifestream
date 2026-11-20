using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Contracts.Lookups;

namespace Lifestream.Client.Lookups;

/// <summary>
/// Behind the record picker control (SHELL-37): a searchable async combo for donor, patient, staff, appointment and request.
/// Typing searches the server after a short pause; while it searches the control shows skeleton rows; after a choice it shows
/// a summary chip with "Change" (and, as a filter, a clear). It holds the chosen id, so no screen ever asks for a typed id.
/// </summary>
public sealed partial class RecordPickerViewModel : ObservableObject, IDisposable
{
    private readonly ILifestreamApiClient _api;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _debounce;
    private CancellationTokenSource? _searchCts;
    private ITimer? _timer;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly IServerStatus? _serverStatus;

    public RecordPickerViewModel(ILifestreamApiClient api, RecordKind kind, TimeProvider? clock = null, TimeSpan? debounce = null, IServerStatus? serverStatus = null)
    {
        _api = api;
        Kind = kind;
        _clock = clock ?? TimeProvider.System;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        Operations = new OperationRunner(() => serverStatus, () => _clock);
        Operations.Changed += (_, _) => Post(() => IsWaking = Operations.IsWaking);
        _serverStatus = serverStatus;
        if (serverStatus is not null)
        {
            serverStatus.Changed += OnServerStatusChanged;
        }
        ChangeCommand = new AsyncRelayCommand((_, _) =>
        {
            Selected = null;
            IsChoosing = true;
            IsOpen = true;
            return SearchNowAsync();
        }, runner: Operations);
        ClearCommand = new AsyncRelayCommand(_ =>
        {
            Selected = null;
            IsChoosing = false;
            IsOpen = false;
            return Task.CompletedTask;
        });
        ChooseCommand = new AsyncRelayCommand((_, _) =>
        {
            IsChoosing = true;
            IsOpen = true;
            return SearchNowAsync();
        }, runner: Operations);
        ChangeCommand.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsBusy));
        ClearCommand.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsBusy));
    }

    public RecordKind Kind { get; }

    /// <summary>
    /// The search box's hint: what the search finds (SearchQueries, the same rule as the lists). A name, a part of a phone number or the record's
    /// code; for a staff member the user name in place of the phone, which is private and not searched.
    /// </summary>
    public string SearchHint => Kind == RecordKind.Staff ? "Search by name, username or ID" : "Search by name, phone or ID";

    /// <summary>The runner of the search: it can be cancelled down to the HTTP request, like every operation (SHELL-07).</summary>
    public OperationRunner Operations { get; }

    public ObservableCollection<RecordOption> Options { get; } = [];

    public AsyncRelayCommand ChangeCommand { get; }

    /// <summary>"Choose" on a filter picker that has nothing chosen: opens the search box and its first page of matches.</summary>
    public AsyncRelayCommand ChooseCommand { get; }

    /// <summary>
    /// The placeholder of a picker used as a filter ("Any donor"). When set, a picker with nothing chosen shows this text and a "Choose" action instead of the search box,
    /// and a chosen record gets a clear (x). Null (the default) keeps the form behaviour: an empty picker is the search box.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowSearch), nameof(IsFilter))]
    public partial string? EmptyText { get; set; }

    /// <summary>True once "Choose" or "Change" opened the search box of a filter picker; "Clear" and a choice close it again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowSearch))]
    public partial bool IsChoosing { get; set; }

    public bool IsFilter => EmptyText is not null;

    /// <summary>A filter picker with nothing chosen and no search open: the placeholder and "Choose".</summary>
    public bool ShowEmpty => Selected is null && EmptyText is not null && !IsChoosing;

    /// <summary>The "Cancel" of the waking state: stops the search request itself.</summary>
    [NoOperationRunner("It only cancels the search in flight; it makes no API call.")]
    public AsyncRelayCommand CancelSearchCommand => field ??= new AsyncRelayCommand(_ =>
    {
        Operations.Cancel();
        return Task.CompletedTask;
    });

    /// <summary>True while a search is waiting on a waking server (after 3 s): the control shows "Waking up the server" with Cancel (SHELL-07).</summary>
    [ObservableProperty]
    public partial bool IsWaking { get; set; }

    [NoOperationRunner("It only clears the chosen record; it makes no API call.")]
    public AsyncRelayCommand ClearCommand { get; }

    /// <summary>The label above the control; set by the screen ("Donor", "Patient"...).</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>True while the search is running: the control shows three skeleton rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    public partial bool IsSearching { get; set; }

    /// <summary>Same state as <see cref="IsSearching"/>, under the name every view model uses (SHELL-11).</summary>
    public bool IsLoading => IsSearching;

    /// <summary>True while "Change" or "Clear" is running.</summary>
    public bool IsBusy => ChangeCommand.IsRunning || ClearCommand.IsRunning;

    [ObservableProperty]
    public partial string? SearchError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedId), nameof(HasSelection), nameof(ShowSearch), nameof(ShowEmpty), nameof(SummaryText))]
    public partial RecordOption? Selected { get; set; }

    public int? SelectedId => Selected?.Id;

    public bool HasSelection => Selected is not null;

    public bool ShowSearch => Selected is null && !ShowEmpty;

    public bool HasNoMatches => !IsSearching && SearchError is null && Options.Count == 0 && SearchText.Length > 0;

    /// <summary>"Ayşe Öztürk · D-1123 · A+" style chip text: name, record code and the key detail.</summary>
    public string SummaryText => Selected is null ? string.Empty : $"{Selected.Title} · {CodeOf(Selected.Id)}" + (string.IsNullOrEmpty(Selected.Subtitle) ? string.Empty : $" · {Selected.Subtitle}");

    /// <summary>The record's code as every list and panel shows it ("D-0041", "P-0002"), never a bare "#41".</summary>
    public string CodeOf(int id) => Kind switch
    {
        RecordKind.Donor => Features.Receipts.RecordCodes.Donor(id),
        RecordKind.Patient => Features.Receipts.RecordCodes.Patient(id),
        RecordKind.Staff => Features.Receipts.RecordCodes.Staff(id),
        RecordKind.Appointment => Features.Receipts.RecordCodes.Appointment(id),
        _ => Features.Receipts.RecordCodes.Request(id),
    };

    partial void OnSearchTextChanged(string value)
    {
        if (Selected is not null)
        {
            return;
        }

        IsOpen = true;
        _timer?.Dispose();
        _timer = _clock.CreateTimer(_ => Post(() => _ = SearchNowAsync()), null, _debounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Selects by id when the screen already knows the record (for example opening "Book appointment" from a donor).</summary>
    public void Select(RecordOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        Selected = option;
        IsChoosing = false;
        IsOpen = false;
        SearchText = string.Empty;
    }

    public async Task SearchNowAsync()
    {
        _timer?.Dispose();
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearching = true;
        SearchError = null;
        try
        {
            var text = SearchText.Trim();
            var result = await Operations.ScopeAsync(ct => _api.SearchRecordsAsync(Kind, text, ct), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Options.Clear();
            if (result.IsOk)
            {
                foreach (var option in result.Value)
                {
                    Options.Add(option);
                }
            }
            else
            {
                SearchError = "Couldn't search just now. Try again.";
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                SearchError = "The search was cancelled. Type again to search.";
            }

            // Otherwise a newer search replaced this one.
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
                OnPropertyChanged(nameof(HasNoMatches));
            }
        }
    }

    private void OnServerStatusChanged(object? sender, EventArgs e) => Operations.StatusChanged();

    private void Post(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    public void Dispose()
    {
        if (_serverStatus is not null)
        {
            _serverStatus.Changed -= OnServerStatusChanged;
        }

        Operations.Dispose();
        _timer?.Dispose();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}
