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
    private DashboardResponse? _fetched;
    private bool _changingPeriod;

    public DashboardViewModel(ILifestreamApiClient api, INavigationService navigation)
    {
        _api = api;
        _navigation = navigation;
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
        _fetched = (await _api.GetDashboardAsync(start, end, offsetMinutes, ct).ConfigureAwait(false)).Unwrap();
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
        RebuildChart();
    }

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
