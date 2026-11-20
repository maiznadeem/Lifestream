using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Navigation;
using Lifestream.Client.Shell;
using Lifestream.Contracts.Dashboard;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Dashboard;

/// <summary>A period of the donations chart.</summary>
public sealed record PeriodOption(string Display, int Months)
{
    public override string ToString() => Display;
}

/// <summary>One headline card. Opening it goes to the matching list, already filtered (FEAT-30).</summary>
public sealed record KpiCard(string Title, int Value, string Glyph, string DestinationTitle, ListPrefilter Prefilter, string Hint)
{
    public string ValueText => Value.ToString("N0", CultureInfo.CurrentCulture);

    public string AutomationName => $"{Title}: {ValueText}. {Hint}";
}

/// <summary>A blood type chip that includes or leaves out that type in the donations chart.</summary>
public sealed partial class BloodTypeToggle(BloodType bloodType, bool isSelected) : ObservableObject
{
    public BloodType BloodType { get; } = bloodType;

    public string Display => BloodType.ToDisplay();

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = isSelected;
}

/// <summary>A row of the totals table: numbers for the chosen period.</summary>
public sealed record TotalsRow(string Label, int Donated, int Issued, bool IsTotal = false)
{
    public string DonatedText => Donated.ToString("N0", CultureInfo.CurrentCulture);

    public string IssuedText => Issued.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>A blood type that is below its threshold: shown with an icon and words, and opens the stock screen on that type.</summary>
public sealed record LowStockAlert(BloodType BloodType, int Available, int Threshold)
{
    public string Text => $"{BloodType.ToDisplay()}: low stock, {Available:N0} of {Threshold:N0} units";
}

/// <summary>How urgent a "Needs attention" item is: its icon tint.</summary>
public enum AttentionKind
{
    Critical,
    Warning,
    Info,
}

/// <summary>
/// One row of "Needs attention": what is wrong, why it matters (who is waiting, by when) and the one action that deals with it. The action opens
/// the screen that holds the work, already on the right record or filter.
/// </summary>
public sealed record AttentionItem(AttentionKind Kind, string Icon, string Title, string Detail, string ActionText, string Destination, ListPrefilter Prefilter)
{
    public string AutomationName => $"{Title}. {Detail}";
}

/// <summary>Where a person booked for today stands.</summary>
public enum TodayState
{
    /// <summary>The donation is recorded.</summary>
    Done,

    /// <summary>The first appointment still to come: the one to check in.</summary>
    Next,

    /// <summary>Later today.</summary>
    Pending,
}

/// <summary>One row of "Today's appointments".</summary>
public sealed record TodayAppointment(int Id, int DonorId, string Time, string DonorName, string Group, TodayState State, bool IsFirstTime)
{
    public bool IsDone => State == TodayState.Done;

    public bool IsNext => State == TodayState.Next;

    public string AutomationName => $"{Time}, {DonorName}, {Group}" + State switch
    {
        TodayState.Done => ", donated",
        TodayState.Next => ", next",
        _ => string.Empty,
    } + (IsFirstTime ? ", first-time donor" : string.Empty);
}

/// <summary>
/// One meter of "Stock by blood group": the units available, the share expiring within 7 days (hatched) and the group's minimum (a tick). The meter
/// reads up to twice the largest minimum, so the minimum sits in the middle and a full group fills the bar.
/// </summary>
public sealed record StockMeter(BloodType BloodType, int Available, int Expiring, int Threshold, int Scale)
{
    public string Group => BloodType.ToTypographic();

    public bool IsLow => Available < Threshold;

    public bool IsEmpty => Available == 0;

    public double FillFraction => Scale <= 0 ? 0 : Math.Min(1, (double)Available / Scale);

    public double ExpiringFraction => Scale <= 0 ? 0 : Math.Min(FillFraction, (double)Expiring / Scale);

    public double MinimumFraction => Scale <= 0 ? 0 : Math.Min(1, (double)Threshold / Scale);

    public string CountText => Available.ToString("N0", CultureInfo.CurrentCulture);

    public string UnitText => Available == 1 ? "unit" : "units";

    public string AutomationName => $"{Group}: {CountText} {UnitText}" + (Expiring > 0 ? $", {Expiring} expiring within 7 days" : string.Empty) + (IsLow ? $", below the minimum of {Threshold}" : string.Empty);
}

/// <summary>One bar of the demographics chart.</summary>
public sealed record GenderBar(Gender Gender, int Count)
{
    public string Label => Gender.ToString();
}

/// <summary>
/// The Dashboard (FEAT-27 to FEAT-31): one API call fills every block. Months are buckets, and the chart's labels, its data and its
/// "View as table" rows all come from the same bucket list, so an axis label can never disagree with its bar (FEAT-28).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML, which needs instance properties.")]
public sealed partial class DashboardViewModel : ViewModelBase
{
    /// <summary>The name of one x-axis bucket: the chart groups by calendar month.</summary>
    public const string BucketName = "Month";

    private readonly ILifestreamApiClient _api;
    private readonly INavigationService _navigation;
    private readonly Session.ISessionService? _session;
    private DashboardResponse? _fetched;
    private IReadOnlyList<Contracts.Stock.StockItem> _stock = [];
    private IReadOnlyList<Contracts.Appointments.AppointmentListItem> _today = [];
    private IReadOnlyList<Contracts.Requests.RequestListItem> _requests = [];
    private HashSet<int> _firstTimers = [];
    private bool _changingPeriod;

    public DashboardViewModel(ILifestreamApiClient api, INavigationService navigation, Session.ISessionService? session = null)
    {
        _api = api;
        _navigation = navigation;
        _session = session;
        RunAttentionCommand = Command(async (parameter, _) =>
        {
            if (parameter is AttentionItem item)
            {
                await OpenAsync(item.Destination, item.Prefilter).ConfigureAwait(true);
            }
        });
        OpenAppointmentCommand = Command(async (parameter, _) =>
        {
            if (parameter is TodayAppointment row)
            {
                await OpenAsync("Appointments", new ListPrefilter(RecordId: row.Id)).ConfigureAwait(true);
            }
        });
        Periods =
        [
            new("Last 3 months", 3),
            new("Last 6 months", 6),
            new("Last 12 months", 12),
            new("Last 24 months", 24),
        ];
        _changingPeriod = true;
        SelectedPeriod = Periods[2]; // the last 12 months
        _changingPeriod = false;
        OpenCardCommand = Command((parameter, _) => OpenCardAsync(parameter));
        OpenLowStockCommand = Command(async (parameter, _) =>
        {
            if (parameter is LowStockAlert alert)
            {
                await OpenAsync("Stock", new ListPrefilter(BloodType: alert.BloodType, StockView: StockFilter.BelowThreshold)).ConfigureAwait(true);
            }
        });
        RetryCommand = Command(ct => LoadAsync(ct));
    }

    public override string PageTitle => "Dashboard";

    public IReadOnlyList<PeriodOption> Periods { get; }

    public ObservableCollection<KpiCard> Cards { get; } = [];

    public ObservableCollection<BloodTypeToggle> BloodTypes { get; } = [];

    public ObservableCollection<TotalsRow> Totals { get; } = [];

    public ObservableCollection<LowStockAlert> LowStock { get; } = [];

    public ObservableCollection<GenderBar> Demographics { get; } = [];

    public AsyncRelayCommand OpenCardCommand { get; }

    public AsyncRelayCommand OpenLowStockCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    /// <summary>Runs the action of a "Needs attention" item (the parameter): opens its screen on the record or filter.</summary>
    public AsyncRelayCommand RunAttentionCommand { get; }

    /// <summary>Opens a row of "Today's appointments" (the parameter) on the Appointments screen.</summary>
    public AsyncRelayCommand OpenAppointmentCommand { get; }

    /// <summary>"Thursday, 19 November 2026": the line above the greeting.</summary>
    public string DateLine => Clock.GetLocalNow().ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>"Good morning, Hana" (afternoon from 12:00, evening from 18:00).</summary>
    public string Greeting
    {
        get
        {
            var hour = Clock.GetLocalNow().Hour;
            var part = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
            var name = _session?.User?.DisplayName?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return string.IsNullOrEmpty(name) ? part : $"{part}, {name}";
        }
    }

    /// <summary>What needs doing now, most urgent first: requests the stock cannot cover, bags about to expire, groups below their minimum, requests due soon.</summary>
    public ObservableCollection<AttentionItem> Attention { get; } = [];

    /// <summary>"5 items" when every row is shown, "4 of 5" when the list is cut to what fits (it always says what is on screen).</summary>
    [ObservableProperty]
    public partial string AttentionCountText { get; set; } = string.Empty;

    /// <summary>The rows of <see cref="Attention"/> that are on screen: all of them, or the first <see cref="AttentionLimit"/> until "View all" is pressed.</summary>
    public ObservableCollection<AttentionItem> VisibleAttention { get; } = [];

    /// <summary>
    /// How many rows the panel has room for (the view sets it from the window's height); zero or less means no limit. The most urgent rows come
    /// first, so those are the ones kept.
    /// </summary>
    public int AttentionLimit
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                ApplyAttentionLimit();
            }
        }
    }

    /// <summary>True after "View all": every row is shown, whatever the limit (the page scrolls if it has to).</summary>
    [ObservableProperty]
    public partial bool IsAttentionExpanded { get; set; }

    /// <summary>True when the list is longer than the limit, so there is something to show or to fold away.</summary>
    [ObservableProperty]
    public partial bool CanToggleAttention { get; set; }

    /// <summary>"View all 5" while rows are hidden, "Show fewer" once they are all shown.</summary>
    [ObservableProperty]
    public partial string AttentionToggleText { get; set; } = string.Empty;

    /// <summary>Shows every row, or folds the list back to what fits. It makes no API call.</summary>
    public System.Windows.Input.ICommand ToggleAttentionCommand => _toggleAttentionCommand ??= new CommunityToolkit.Mvvm.Input.RelayCommand(() => IsAttentionExpanded = !IsAttentionExpanded);

    private System.Windows.Input.ICommand? _toggleAttentionCommand;

    partial void OnIsAttentionExpandedChanged(bool value) => ApplyAttentionLimit();

    /// <summary>Fills <see cref="VisibleAttention"/> from <see cref="Attention"/> and words the count and the toggle for what is shown.</summary>
    private void ApplyAttentionLimit()
    {
        var total = Attention.Count;
        var limit = AttentionLimit <= 0 ? int.MaxValue : AttentionLimit;
        var canToggle = total > limit;
        var shown = canToggle && !IsAttentionExpanded ? limit : total;
        while (VisibleAttention.Count > shown)
        {
            VisibleAttention.RemoveAt(VisibleAttention.Count - 1);
        }

        for (var i = 0; i < shown; i++)
        {
            if (i >= VisibleAttention.Count)
            {
                VisibleAttention.Add(Attention[i]);
            }
            else if (!ReferenceEquals(VisibleAttention[i], Attention[i]))
            {
                VisibleAttention[i] = Attention[i];
            }
        }

        CanToggleAttention = canToggle;
        AttentionToggleText = !canToggle ? string.Empty : IsAttentionExpanded ? "Show fewer" : $"View all {total}";
        AttentionCountText = total switch
        {
            0 => string.Empty,
            _ when shown < total => $"{shown} of {total}",
            1 => "1 item",
            _ => $"{total} items",
        };
    }

    /// <summary>Today's appointments in time order (cancelled ones left out).</summary>
    public ObservableCollection<TodayAppointment> Today { get; } = [];

    /// <summary>"3 of 8 seen".</summary>
    [ObservableProperty]
    public partial string TodaySeenText { get; set; } = string.Empty;

    /// <summary>One meter per blood group, in the usual order.</summary>
    public ObservableCollection<StockMeter> StockMeters { get; } = [];

    /// <summary>"37 units".</summary>
    [ObservableProperty]
    public partial string StockTotalText { get; set; } = string.Empty;

    /// <summary>"Minimum, 10 units" (the legend of the meters' tick).</summary>
    [ObservableProperty]
    public partial string MinimumText { get; set; } = string.Empty;

    /// <summary>The x-axis labels of the 12-month line chart: the month, with the year under it on the first month and on every January.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ChartMonthLabels { get; set; } = [];

    [ObservableProperty]
    public partial PeriodOption SelectedPeriod { get; set; } = null!;

    /// <summary>The x-axis labels, one per month bucket, made from the same month values as the data.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> MonthLabels { get; set; } = [];

    /// <summary>Units donated per month bucket, for the blood types that are switched on.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<int> DonatedByMonth { get; set; } = [];

    /// <summary>Units issued per month bucket, for the blood types that are switched on.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<int> IssuedByMonth { get; set; } = [];

    /// <summary>The chart's data as a table, for "View as table" (SHELL-35).</summary>
    [ObservableProperty]
    public partial ChartTable DonationsTable { get; set; } = new([], []);

    [ObservableProperty]
    public partial ChartTable DemographicsTable { get; set; } = new([], []);

    [ObservableProperty]
    public partial bool HasLowStock { get; set; }

    /// <summary>The y-axis starts at zero so bar heights are honest (FEAT-28).</summary>
    public static double YAxisMinimum => 0;

    public string XAxisTitle => BucketName;

    public string YAxisTitle => "Units";

    /// <summary>Raised when the chart data changed (a new load, a period, a blood type toggle) so the view redraws.</summary>
    public event EventHandler? ChartDataChanged;

    protected override bool HasNoData => false;

    /// <summary>
    /// A dashboard filled from a small made-up response without any API call. The app draws one off the UI thread at startup (the desktop's chart warm-up) so the first real
    /// dashboard, which is drawn right after sign-in, finds the code it runs already compiled (FEAT-27).
    /// </summary>
    public static DashboardViewModel CreateWarmUpSample()
    {
        var model = new DashboardViewModel(null!, null!);
        var months = new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1) };
        var stock = new Contracts.Stock.StockItem(BloodType.ANeg, 4, 1, 10, true);
        model._fetched = new DashboardResponse(
            new DashboardKpis(1, 1, 1, 1, 1, 1),
            months,
            [.. SharedLists.BloodTypes.Select(c => new BloodTypeSeries(c.Value, [3, 5, 4], [2, 4, 1]))],
            [.. SharedLists.Genders.Select(c => new GenderCount(c.Value, 1))],
            [stock]);
        model.Apply();
        model.State = LoadState.Ready;
        return model;
    }

    /// <summary>The month label used for both the axis and the table: "Mar 2026".</summary>
    public static string MonthLabel(DateOnly month) => month.ToString("MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The inclusive range a period covers: its first month's first day up to today (FEAT-28).</summary>
    public static (DateOnly Start, DateOnly End) RangeFor(int months, DateOnly today) =>
        (new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1)), today);

    protected override async Task FetchAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(Clock.GetLocalNow().DateTime);
        var (start, end) = RangeFor(SelectedPeriod.Months, today);
        // The dashboard's months and days are the user's local calendar: say how far it is from UTC so a record at 23:30 local lands on the right day.
        var offsetMinutes = (int)Clock.GetLocalNow().Offset.TotalMinutes;
        var dashboard = _api.GetDashboardAsync(start, end, offsetMinutes, ct);
        // The blocks of direction A's dashboard come from the existing endpoints, in parallel with the dashboard call. Each is best effort: a block whose
        // call failed shows its empty state, and only the dashboard call itself can fail the screen.
        var day = Appointments.LocalDay.Of(Clock);
        var stock = Quietly(() => _api.GetStockAsync(ct));
        var todays = Quietly(() => _api.GetAppointmentsAsync(day.Query(sort: Contracts.SortKeys.Slot), ct));
        var requests = Quietly(() => _api.GetRequestsAsync(new Contracts.Requests.RequestQuery(Status: RequestStatus.Pending, PageSize: 50, Sort: Contracts.SortKeys.RequiredBy), ct));
        _fetched = (await dashboard.ConfigureAwait(false)).Unwrap();
        _stock = (await stock.ConfigureAwait(false)) is { IsOk: true } s ? s.Value : [];
        _today = (await todays.ConfigureAwait(false)) is { IsOk: true } t ? [.. t.Value.Items.Where(a => a.Status != AppointmentStatus.Cancelled && day.Holds(a)).OrderBy(a => a.SlotStartUtc)] : [];
        _requests = (await requests.ConfigureAwait(false)) is { IsOk: true } r ? r.Value.Items : [];

        // First-time donors among those still to come. The appointment list says so itself (no completed donation before the slot), so the dashboard is
        // four requests, one per data set, however many people come today: it does not ask for each donor.
        _firstTimers = [.. _today.Where(a => a.Status == AppointmentStatus.Pending && a.DonorFirstTime).Select(a => a.DonorId)];
    }

    /// <summary>Runs a secondary call; a failure or an exception becomes null instead of failing the screen.</summary>
    private static async Task<ApiResult<T>?> Quietly<T>(Func<Task<ApiResult<T>>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    protected override void Apply()
    {
        if (_fetched is not { } data)
        {
            return;
        }

        Cards.Clear();
        var k = data.Kpis;
        Cards.Add(new("Donors", k.TotalDonors, "", "Donors", new(), "Open the donors list"));
        Cards.Add(new("Patients", k.TotalPatients, "", "Patients", new(), "Open the patients list"));
        Cards.Add(new("Pending appointments", k.PendingAppointments, "", "Appointments", new(Status: "Pending"), "Open pending appointments"));
        Cards.Add(new("Pending requests", k.PendingRequests, "", "Requests", new(Status: "Pending"), "Open pending requests"));
        Cards.Add(new("Units in stock", k.UnitsAvailable, "", "Stock", new(), "Open the stock tiles"));
        Cards.Add(new("Expiring within 7 days", k.UnitsExpiringSoon, "", "Stock", new(StockView: StockFilter.ExpiringSoon), "Open the stock tiles showing expiring units"));

        if (BloodTypes.Count == 0)
        {
            foreach (var type in SharedLists.BloodTypes.Select(c => c.Value))
            {
                var toggle = new BloodTypeToggle(type, true);
                toggle.PropertyChanged += (_, _) => RebuildChart();
                BloodTypes.Add(toggle);
            }
        }

        LowStock.Clear();
        foreach (var item in data.LowStock.Where(i => i.BelowThreshold))
        {
            LowStock.Add(new(item.BloodType, item.Available, item.Threshold));
        }

        HasLowStock = LowStock.Count > 0;

        Demographics.Clear();
        foreach (var bar in data.Demographics)
        {
            Demographics.Add(new(bar.Gender, bar.Count));
        }

        DemographicsTable = new(["Gender", "Donors"], [.. Demographics.Select(b => (IReadOnlyList<string>)[b.Label, b.Count.ToString("N0", CultureInfo.CurrentCulture)])]);
        ApplyStock();
        ApplyToday();
        ApplyAttention();
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(DateLine));
        RebuildChart();
    }

    private void ApplyStock()
    {
        StockMeters.Clear();
        var items = _stock.Count > 0 ? _stock : _fetched?.LowStock ?? [];
        var threshold = items.Count > 0 ? items.Max(i => i.Threshold) : 0;
        var scale = Math.Max(threshold * 2, items.Count > 0 ? items.Max(i => i.Available) : 0);
        foreach (var item in items.OrderBy(i => i.BloodType))
        {
            StockMeters.Add(new StockMeter(item.BloodType, item.Available, item.ExpiringSoon, item.Threshold, scale));
        }

        var total = items.Sum(i => i.Available);
        StockTotalText = items.Count == 0 ? string.Empty : $"{total.ToString("N0", CultureInfo.CurrentCulture)} {(total == 1 ? "unit" : "units")}";
        MinimumText = threshold > 0 ? $"Minimum, {threshold.ToString("N0", CultureInfo.CurrentCulture)} units" : string.Empty;
    }

    private string LocalTime(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Clock.LocalTimeZone).ToString("HH:mm", CultureInfo.CurrentCulture);

    private void ApplyToday()
    {
        Today.Clear();
        var nextFound = false;
        foreach (var a in _today)
        {
            var state = a.Status == AppointmentStatus.Completed ? TodayState.Done : TodayState.Pending;
            if (state == TodayState.Pending && !nextFound && a.Status == AppointmentStatus.Pending)
            {
                state = TodayState.Next;
                nextFound = true;
            }

            Today.Add(new TodayAppointment(a.Id, a.DonorId, LocalTime(a.SlotStartUtc), a.DonorName, a.DonorBloodType.ToTypographic(), state, _firstTimers.Contains(a.DonorId)));
        }

        TodaySeenText = Today.Count == 0 ? string.Empty : $"{Today.Count(t => t.IsDone)} of {Today.Count} seen";
    }

    /// <summary>
    /// Builds "Needs attention" from the stock, the open requests and today's appointments (direction A): a request that no compatible stock can cover,
    /// bags expiring within 7 days (and the request that could use them), the groups below their minimum, and the next request that is due within
    /// 3 days and can be fulfilled.
    /// </summary>
    private void ApplyAttention()
    {
        Attention.Clear();
        var today = DateOnly.FromDateTime(Clock.GetLocalNow().DateTime);
        var available = _stock.ToDictionary(i => i.BloodType, i => i.Available);
        int Cover(BloodType patient) => available.Where(p => p.Key.CanGiveTo(patient)).Sum(p => p.Value);
        static string Day(DateOnly d) => d.ToString("ddd d MMM", CultureInfo.CurrentCulture);

        foreach (var request in _requests.Where(r => Cover(r.BloodType) < r.Units).OrderBy(r => r.RequiredBy).Take(2))
        {
            var cover = Cover(request.BloodType);
            var title = available.GetValueOrDefault(request.BloodType) == 0 ? $"{request.BloodType.ToTypographic()} is out of stock" : $"Not enough blood for {request.PatientName}";
            var detail = $"{request.PatientName} needs {Units(request.Units)} by {Day(request.RequiredBy)}" + (cover > 0 ? $"; {Units(cover)} can be given." : ".");
            var donor = Today.FirstOrDefault(t => t.State != TodayState.Done && _today.First(a => a.Id == t.Id).DonorBloodType.CanGiveTo(request.BloodType));
            if (donor is not null)
            {
                detail += $" {donor.DonorName} ({donor.Group}) comes in at {donor.Time}.";
            }

            Attention.Add(new(AttentionKind.Critical, "Alert", title, detail, "Open request", "Requests", new ListPrefilter(RecordId: request.Id)));
        }

        foreach (var item in _stock.Where(i => i.ExpiringSoon > 0).OrderByDescending(i => i.ExpiringSoon).Take(1))
        {
            var detail = "Use them first.";
            var user = _requests.Where(r => item.BloodType.CanGiveTo(r.BloodType)).OrderBy(r => r.BloodType == item.BloodType ? 0 : 1).ThenBy(r => r.RequiredBy).FirstOrDefault();
            if (user is not null)
            {
                detail = $"Use them for {user.PatientName}’s request ({Units(user.Units)}, due {user.RequiredBy.ToString("ddd", CultureInfo.CurrentCulture)}).";
            }

            Attention.Add(new(AttentionKind.Warning, "Clock", $"{item.ExpiringSoon} {item.BloodType.ToTypographic()} {(item.ExpiringSoon == 1 ? "bag expires" : "bags expire")} within 7 days", detail,
                "Review bags", "Stock", new ListPrefilter(BloodType: item.BloodType, StockView: StockFilter.ExpiringSoon)));
        }

        var low = _stock.Where(i => i.BelowThreshold).OrderBy(i => i.BloodType).ToList();
        if (low.Count > 0)
        {
            var threshold = low.Max(i => i.Threshold);
            Attention.Add(new(AttentionKind.Critical, "Stock", low.Count == 1 ? $"{low[0].BloodType.ToTypographic()} is below {threshold} units" : $"{low.Count} of {_stock.Count} blood groups are below {threshold} units",
                string.Join(", ", low.Select(i => $"{i.BloodType.ToTypographic()} {i.Available}")) + ".", "Open stock", "Stock", new ListPrefilter(StockView: StockFilter.BelowThreshold)));
        }

        foreach (var request in _requests.Where(r => Cover(r.BloodType) >= r.Units && r.RequiredBy <= today.AddDays(3)).OrderBy(r => r.RequiredBy).Take(1))
        {
            var due = request.RequiredBy == today ? "today" : request.RequiredBy == today.AddDays(1) ? "tomorrow" : $"on {request.RequiredBy.ToString("dddd", CultureInfo.CurrentCulture)}";
            var same = available.GetValueOrDefault(request.BloodType);
            Attention.Add(new(AttentionKind.Info, "Request", $"{request.PatientName}’s request is due {due}",
                $"{request.BloodType.ToTypographic()}, {Units(request.Units)}. {same} {request.BloodType.ToTypographic()} {(same == 1 ? "bag is" : "bags are")} available.", "Fulfil", "Requests", new ListPrefilter(RecordId: request.Id)));
        }

        ApplyAttentionLimit();
    }

    private static string Units(int n) => n == 1 ? "1 unit" : $"{n.ToString("N0", CultureInfo.CurrentCulture)} units";

    private void RebuildChart()
    {
        if (_fetched is not { } data)
        {
            return;
        }

        var selected = BloodTypes.Where(t => t.IsSelected).Select(t => t.BloodType).ToHashSet();
        var months = data.Months.Count;
        var donated = new int[months];
        var issued = new int[months];
        foreach (var series in data.Series.Where(s => selected.Contains(s.BloodType)))
        {
            for (var i = 0; i < months; i++)
            {
                donated[i] += i < series.Donated.Length ? series.Donated[i] : 0;
                issued[i] += i < series.Issued.Length ? series.Issued[i] : 0;
            }
        }

        MonthLabels = [.. data.Months.Select(MonthLabel)];
        ChartMonthLabels = [.. data.Months.Select((m, i) => i == 0 || m.Month == 1 ? m.ToString("MMM", CultureInfo.InvariantCulture) + "\n" + m.Year.ToString(CultureInfo.InvariantCulture) : m.ToString("MMM", CultureInfo.InvariantCulture))];
        DonatedByMonth = donated;
        IssuedByMonth = issued;
        DonationsTable = new(
            [BucketName, "Donated", "Issued"],
            [.. Enumerable.Range(0, months).Select(i => (IReadOnlyList<string>)[MonthLabels[i], donated[i].ToString("N0", CultureInfo.CurrentCulture), issued[i].ToString("N0", CultureInfo.CurrentCulture)])]);

        Totals.Clear();
        foreach (var series in data.Series.Where(s => selected.Contains(s.BloodType)).OrderBy(s => s.BloodType))
        {
            Totals.Add(new(series.BloodType.ToDisplay(), series.Donated.Sum(), series.Issued.Sum()));
        }

        if (Totals.Count > 0)
        {
            Totals.Add(new("Total", Totals.Sum(t => t.Donated), Totals.Sum(t => t.Issued), IsTotal: true));
        }

        ChartDataChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedPeriodChanged(PeriodOption value)
    {
        if (!_changingPeriod && value is not null)
        {
            PendingLoad = LoadAsync(); // the period is part of the one call, so a new period is a new call
        }
    }

    /// <summary>Completes when the load started by the last period change has finished (for tests).</summary>
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    private Task OpenCardAsync(object? parameter) =>
        parameter is KpiCard card ? OpenAsync(card.DestinationTitle, card.Prefilter) : Task.CompletedTask;

    private async Task OpenAsync(string destinationTitle, ListPrefilter prefilter)
    {
        var destination = NavigationCatalog.Default.FirstOrDefault(d => d.Title == destinationTitle);
        if (destination is not null)
        {
            await _navigation.NavigateTo(destination.ViewModelType, prefilter).ConfigureAwait(true);
        }
    }
}
