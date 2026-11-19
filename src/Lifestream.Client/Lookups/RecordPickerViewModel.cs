using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Contracts.Lookups;

namespace Lifestream.Client.Lookups;

/// <summary>
/// Behind the record picker control (SHELL-37): a searchable async combo for donor, patient, staff, appointment and request.
/// Typing searches the server after a short pause; while it searches the control shows skeleton rows; after a choice it shows
/// a summary chip with "Change". It holds the chosen id, so no screen ever asks for a typed id.
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
            IsOpen = true;
            return SearchNowAsync();
        }, runner: Operations);
        ClearCommand = new AsyncRelayCommand(_ =>
        {
            Selected = null;
            return Task.CompletedTask;
        });
        ChangeCommand.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsBusy));
        ClearCommand.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsBusy));
    }

    public RecordKind Kind { get; }

    /// <summary>The runner of the search: it can be cancelled down to the HTTP request, like every operation (SHELL-07).</summary>
    public OperationRunner Operations { get; }

    public ObservableCollection<RecordOption> Options { get; } = [];

    public AsyncRelayCommand ChangeCommand { get; }

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
    [NotifyPropertyChangedFor(nameof(SelectedId), nameof(HasSelection), nameof(ShowSearch), nameof(SummaryText))]
    public partial RecordOption? Selected { get; set; }

    public int? SelectedId => Selected?.Id;

    public bool HasSelection => Selected is not null;

    public bool ShowSearch => Selected is null;

    public bool HasNoMatches => !IsSearching && SearchError is null && Options.Count == 0 && SearchText.Length > 0;

    /// <summary>"Ayşe Öztürk · #1123 · A+" style chip text: name, id and the key detail.</summary>
    public string SummaryText => Selected is null ? string.Empty : $"{Selected.Title} · #{Selected.Id}" + (string.IsNullOrEmpty(Selected.Subtitle) ? string.Empty : $" · {Selected.Subtitle}");

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
