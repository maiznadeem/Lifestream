using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Shell;

/// <summary>
/// The notifications of the title bar's bell: a flyout that lists what needs attention now, the same items as the dashboard's "Needs attention"
/// (<see cref="AttentionBuilder"/>): requests the stock cannot cover, bags about to expire, groups below their minimum, requests due soon. Each item has
/// its one action, which opens the screen that holds the work on the right record or filter. It loads when it opens, so it is current on any
/// screen; the dashboard also hands it its own list whenever it loads, so the bell's dot is right before the flyout was ever opened.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML, which needs instance properties.")]
public sealed partial class NotificationsViewModel : ViewModelBase
{
    private readonly ILifestreamApiClient? _api;
    private readonly Func<AttentionItem, Task>? _run;
    private readonly Func<Task>? _viewAll;
    private IReadOnlyList<AttentionItem> _fetched = [];

    /// <param name="api">The API client; null in a shell built without one (the flyout then shows only what the dashboard handed it).</param>
    /// <param name="run">Opens the screen of an item on its record or filter (the shell's navigation).</param>
    /// <param name="viewAll">Opens the dashboard.</param>
    public NotificationsViewModel(ILifestreamApiClient? api, Func<AttentionItem, Task>? run = null, Func<Task>? viewAll = null)
        : base(LoadState.Loading)
    {
        _api = api;
        _run = run;
        _viewAll = viewAll;
        ToggleCommand = new AsyncRelayCommand(_ =>
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }

            return Task.CompletedTask;
        });
        CloseCommand = new AsyncRelayCommand(_ =>
        {
            Close();
            return Task.CompletedTask;
        });
        RunCommand = Command(async (parameter, _) =>
        {
            if (parameter is AttentionItem item)
            {
                Close();
                if (_run is not null)
                {
                    await _run(item).ConfigureAwait(true);
                }
            }
        });
        ViewAllCommand = Command(async _ =>
        {
            Close();
            if (_viewAll is not null)
            {
                await _viewAll().ConfigureAwait(true);
            }
        });
        RetryCommand = Command(ct => LoadAsync(ct));
    }

    public override string PageTitle => "Notifications";

    /// <summary>True while the flyout is shown.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>What needs attention, most urgent first.</summary>
    public ObservableCollection<AttentionItem> Items { get; } = [];

    /// <summary>True once the list is known (from a load of the flyout or from the dashboard): only then does <see cref="Count"/> say anything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool IsKnown { get; set; }

    /// <summary>How many items need attention (zero until <see cref="IsKnown"/>).</summary>
    public int Count => Items.Count;

    /// <summary>"4 items" beside the flyout's title; empty when there is nothing.</summary>
    public string CountText => Items.Count switch
    {
        0 => string.Empty,
        1 => "1 item",
        var n => $"{n} items",
    };

    /// <summary>The bell's name and tooltip: "Notifications, 4 items need attention".</summary>
    public string Summary => !IsKnown ? "Notifications" : Items.Count switch
    {
        0 => "Notifications, nothing needs attention",
        1 => "Notifications, 1 item needs attention",
        var n => $"Notifications, {n} items need attention",
    };

    /// <summary>The words of the empty state.</summary>
    public string EmptyText => "Nothing needs attention";

    public string EmptyDetail => "Requests, stock and expiring bags are all in order.";

    public string ViewAllText => "View all on the dashboard";

    /// <summary>The bell: opens the flyout, or closes it when it is open.</summary>
    [NoOperationRunner("It shows or hides the flyout; the flyout's own load runs the calls.")]
    public AsyncRelayCommand ToggleCommand { get; }

    /// <summary>Esc and a click outside the flyout.</summary>
    [NoOperationRunner("It hides the flyout; it makes no API call.")]
    public AsyncRelayCommand CloseCommand { get; }

    /// <summary>Runs the action of an item (the parameter): closes the flyout and opens the item's screen on its record or filter.</summary>
    public AsyncRelayCommand RunCommand { get; }

    /// <summary>"View all on the dashboard".</summary>
    public AsyncRelayCommand ViewAllCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    /// <summary>Completes when the load started by the last <see cref="Open"/> has finished (tests).</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Shows the flyout and loads the list again, so it is current on whatever screen the person is.</summary>
    public void Open()
    {
        IsOpen = true;
        if (_api is not null)
        {
            PendingLoad = LoadAsync();
        }
        else if (!IsKnown)
        {
            Publish([]);
        }
    }

    public void Close()
    {
        IsOpen = false;
        CancelLoad();
    }

    /// <summary>Takes the list the dashboard has just built, so the bell's dot is right without a second round of calls.</summary>
    public void Publish(IReadOnlyList<AttentionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Show(items);
        if (State is not (LoadState.Loading or LoadState.Waking) || !IsOpen)
        {
            State = Items.Count == 0 ? LoadState.Empty : LoadState.Ready;
        }
    }

    protected override async Task FetchAsync(CancellationToken ct)
    {
        // The three lists the dashboard reads for "Needs attention", asked the same way. A failed call is an error here, never an empty list: a bell
        // that says "Nothing needs attention" because the server did not answer would be worse than no bell.
        var api = _api!;
        var day = LocalDay.Of(Clock);
        var stock = api.GetStockAsync(ct);
        var todays = api.GetAppointmentsAsync(day.Query(sort: Contracts.SortKeys.Slot), ct);
        var requests = api.GetRequestsAsync(new RequestQuery(Status: RequestStatus.Pending, PageSize: 50, Sort: Contracts.SortKeys.RequiredBy), ct);
        IReadOnlyList<StockItem> stockItems = (await stock.ConfigureAwait(false)).Unwrap();
        IReadOnlyList<AppointmentListItem> today = [.. (await todays.ConfigureAwait(false)).Unwrap().Items.Where(a => a.Status != AppointmentStatus.Cancelled && day.Holds(a)).OrderBy(a => a.SlotStartUtc)];
        IReadOnlyList<RequestListItem> open = (await requests.ConfigureAwait(false)).Unwrap().Items;
        _fetched = AttentionBuilder.Build(stockItems, open, today, Clock);
    }

    protected override void Apply() => Show(_fetched);

    protected override bool HasNoData => Items.Count == 0;

    private void Show(IReadOnlyList<AttentionItem> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        IsKnown = true;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(Summary));
    }
}
