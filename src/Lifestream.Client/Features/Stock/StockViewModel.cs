using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Navigation;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Stock;

/// <summary>How a tile is flagged. The view shows an icon, the words and a token colour for it, so colour is never the only sign (FEAT-19).</summary>
public enum StockSeverity
{
    InStock,
    ExpiringSoon,
    LowStock,
}

/// <summary>One blood type's tile: units available, units expiring within 7 days and whether it is below its threshold.</summary>
public sealed record StockTile(BloodType BloodType, int Available, int ExpiringSoon, int Threshold, bool BelowThreshold)
{
    private static readonly IReadOnlyList<StockAgeCell> EmptyCells = [.. StockAges.Bands.Select((_, i) => new StockAgeCell(i, null, i == StockAges.BandCount - 1))];

    /// <summary>The row of the stock-by-age matrix: one cell per band of <see cref="StockAges.Bands"/> (en dashes when the answer had no bands).</summary>
    public IReadOnlyList<StockAgeCell> AgeCells { get; init; } = EmptyCells;

    public string BloodTypeText => BloodType.ToDisplay();

    public StockSeverity Severity => BelowThreshold ? StockSeverity.LowStock : ExpiringSoon > 0 ? StockSeverity.ExpiringSoon : StockSeverity.InStock;

    /// <summary>The words that go with the icon.</summary>
    public string StatusText => Severity switch
    {
        StockSeverity.LowStock => "Low stock",
        StockSeverity.ExpiringSoon => "Some expiring soon",
        _ => "In stock",
    };

    public string AvailableText => Available == 1 ? "1 unit available" : $"{Available:N0} units available";

    public string ExpiringText => ExpiringSoon == 0 ? $"None expiring within {BloodRules.ExpiringSoonDays} days" : $"{ExpiringSoon:N0} expiring within {BloodRules.ExpiringSoonDays} days";

    public string ThresholdText => $"Low stock below {Threshold:N0} units";

    public string AutomationName => $"{BloodTypeText}: {AvailableText}, {ExpiringText}, {StatusText}";

    /// <summary>The group as set in type: a real minus sign (A−).</summary>
    public string GroupText => BloodType.ToTypographic();

    /// <summary>Units with more than <see cref="BloodRules.ExpiringSoonDays"/> days of shelf life left.</summary>
    public int Fresh => Math.Max(0, Available - ExpiringSoon);

    /// <summary>How many units the group lacks to reach its minimum (0 when it is at or above it).</summary>
    public int ShortBy => Math.Max(0, Threshold - Available);

    public bool IsOut => Available == 0;

    public bool IsLow => BelowThreshold;

    public bool HasExpiring => ExpiringSoon > 0;

    /// <summary>The words of the row's status badge: out, how many short, how many expiring, or in stock.</summary>
    public string BadgeText => IsOut ? "Out of stock" : BelowThreshold ? $"Low, {ShortBy:N0} short" : ExpiringSoon > 0 ? $"{ExpiringSoon:N0} expiring" : "In stock";

    public string UnitsNumber => Available.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>"17 units": the count as one size, number and word.</summary>
    public string UnitsText => Available == 1 ? "1 unit" : $"{UnitsNumber} units";

    public string UnitWord => Available == 1 ? "unit" : "units";

    public string FreshText => Fresh == 0 ? "None" : Fresh.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public string ExpiringNumber => ExpiringSoon == 0 ? "None" : ExpiringSoon.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The meter's full width in units (shared by every row, so the bars compare); 0 hides the fill.</summary>
    public double Scale { get; init; }

    public double FillFraction => Scale <= 0 ? 0 : Math.Min(1, Available / Scale);

    public double ExpiringFraction => Scale <= 0 ? 0 : Math.Min(1, ExpiringSoon / Scale);

    public double MinimumFraction => Scale <= 0 ? -1 : Math.Min(1, Threshold / Scale);

    /// <summary>The groups whose patients can be given this group's red cells.</summary>
    public string GivesToText => string.Join(", ", SharedLists.BloodTypes.Select(c => c.Value).Where(t => BloodType.CanGiveTo(t)).Select(t => t.ToTypographic()));

    /// <summary>The groups whose red cells patients of this group can be given.</summary>
    public string ReceivesFromText => string.Join(", ", SharedLists.BloodTypes.Select(c => c.Value).Where(t => t.CanGiveTo(BloodType)).Select(t => t.ToTypographic()));

    /// <summary>One sentence on where the group stands against its minimum.</summary>
    public string MinimumSentence => IsOut
        ? $"None in stock. The minimum is {Threshold:N0} units."
        : BelowThreshold
            ? $"{ShortBy:N0} {(ShortBy == 1 ? "unit" : "units")} short of the minimum of {Threshold:N0}."
            : $"At or above the minimum of {Threshold:N0} units.";

    internal static StockTile From(StockItem item, double scale = 0) => new(item.BloodType, item.Available, item.ExpiringSoon, item.Threshold, item.BelowThreshold)
    {
        Scale = scale,
        AgeCells = item.AgeBands is { Count: StockAges.BandCount } bands
            ? [.. bands.Select((count, i) => new StockAgeCell(i, count, i == StockAges.BandCount - 1))]
            : EmptyCells,
    };
}

/// <summary>
/// The Stock screen (FEAT-19): one tile per blood type from a single call. Its filters work on the tiles already loaded, so they apply the
/// moment they change, with no extra call.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML, which needs instance properties.")]
public sealed partial class StockViewModel : ViewModelBase, INavigationParameterReceiver
{
    private readonly ILifestreamApiClient _api;
    private readonly INavigationService? _navigation;
    private readonly ListViewState _state;
    private IReadOnlyList<StockItem> _fetched = [];

    /// <summary>Creates the screen; <paramref name="navigation"/> opens the donors and requests of a group from its panel (without it those two actions are hidden).</summary>
    public StockViewModel(ILifestreamApiClient api, IListViewStateStore states, INavigationService? navigation = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        _api = api;
        _navigation = navigation;
        _state = states.GetState("stock");
    }

    public override string PageTitle => "Stock";

    private static readonly IReadOnlyList<FilterOption<BloodType>> BloodTypeChoices = SharedLists.WithAll(SharedLists.BloodTypes, "All blood types");

    private static readonly IReadOnlyList<FilterOption<StockFilter>> ViewChoices = FilterOptions.WithAll(
        [StockFilter.BelowThreshold, StockFilter.ExpiringSoon],
        f => f == StockFilter.BelowThreshold ? "Below threshold" : "Expiring soon",
        "All stock");

    public IReadOnlyList<FilterOption<BloodType>> BloodTypeOptions => BloodTypeChoices;

    public IReadOnlyList<FilterOption<StockFilter>> ViewOptions => ViewChoices;

    /// <summary>The tiles that pass the filters.</summary>
    public ObservableCollection<StockTile> Tiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    public partial FilterOption<BloodType> BloodTypeFilter { get; set; } = BloodTypeChoices[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    public partial FilterOption<StockFilter> ViewFilter { get; set; } = ViewChoices[0];

    /// <summary>How many blood types are below their threshold (the heading says so in words).</summary>
    [ObservableProperty]
    public partial int LowCount { get; set; }

    [ObservableProperty]
    public partial int ExpiringCount { get; set; }

    [ObservableProperty]
    public partial bool NothingMatches { get; set; }

    public bool HasFilters => BloodTypeFilter.Value is not null || ViewFilter.Value is not null;

    // ---- the page header, the view tabs and the group filter chip ----------------------------------------------------

    /// <summary>Units in stock across every group.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitsCountText))]
    public partial int TotalUnits { get; set; }

    /// <summary>Units expiring within the expiring-soon window, across every group.</summary>
    [ObservableProperty]
    public partial int TotalExpiring { get; set; }

    /// <summary>The page header's count: "37 units".</summary>
    public string UnitsCountText => TotalUnits == 1 ? "1 unit" : $"{TotalUnits:N0} units";

    /// <summary>How many groups there are (the All tab's count).</summary>
    [ObservableProperty]
    public partial int GroupCount { get; set; }

    /// <summary>The minimum, stated once for the whole table: "Minimum 10 units per group" (or that it differs by group).</summary>
    [ObservableProperty]
    public partial string MinimumText { get; set; } = string.Empty;

    /// <summary>The line under the page header: how many groups are below their minimum, and which are out.</summary>
    [ObservableProperty]
    public partial string ShortageText { get; set; } = string.Empty;

    public bool HasShortage => LowCount > 0;

    /// <summary>The All tab.</summary>
    public bool IsAllView
    {
        get => ViewFilter.Value is null;
        set
        {
            if (value)
            {
                ViewFilter = ViewOptions[0];
            }
            else
            {
                OnPropertyChanged(nameof(IsAllView)); // a tab unchecked by its group: re-read which view is shown
            }
        }
    }

    /// <summary>The Low tab: groups below their minimum.</summary>
    public bool IsLowView
    {
        get => ViewFilter.Value == StockFilter.BelowThreshold;
        set
        {
            if (value)
            {
                ViewFilter = ViewOptions.First(o => o.Value == StockFilter.BelowThreshold);
            }
            else
            {
                OnPropertyChanged(nameof(IsLowView)); // a tab unchecked by its group: re-read which view is shown
            }
        }
    }

    /// <summary>The Expiring tab: groups with units expiring soon.</summary>
    public bool IsExpiringView
    {
        get => ViewFilter.Value == StockFilter.ExpiringSoon;
        set
        {
            if (value)
            {
                ViewFilter = ViewOptions.First(o => o.Value == StockFilter.ExpiringSoon);
            }
            else
            {
                OnPropertyChanged(nameof(IsExpiringView)); // a tab unchecked by its group: re-read which view is shown
            }
        }
    }

    /// <summary>The filter chip's words: "Blood group" while every group shows, "Blood group: O−" when one is chosen.</summary>
    public string BloodTypeChipText => BloodTypeFilter.Value is { } type ? $"Blood group: {type.ToTypographic()}" : "Blood group";

    public bool HasBloodTypeFilter => BloodTypeFilter.Value is not null;

    /// <summary>Chooses a group from the chip's menu (the parameter is one of <see cref="BloodTypeOptions"/>).</summary>
    public AsyncRelayCommand ChooseBloodTypeCommand => field ??= Command((parameter, _) =>
    {
        if (parameter is FilterOption<BloodType> option)
        {
            BloodTypeFilter = option;
        }

        return Task.CompletedTask;
    });

    // ---- the selected group's panel ----------------------------------------------------------------------------------

    /// <summary>The group whose panel is open, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    [NotifyPropertyChangedFor(nameof(PanelOverline))]
    public partial StockTile? SelectedTile { get; set; }

    public bool IsPanelOpen => SelectedTile is not null;

    // ---- the bags of the selected group (the panel's list, use first at the top) ---------------------------------------

    private CancellationTokenSource? _bagsLoad;

    /// <summary>The selected group's available bags, oldest collection first: the order to use them in, and the order a fulfil issues them.</summary>
    public ObservableCollection<StockBagRow> Bags { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBagsSkeleton))]
    [NotifyPropertyChangedFor(nameof(HasBagsError))]
    [NotifyPropertyChangedFor(nameof(HasBags))]
    [NotifyPropertyChangedFor(nameof(HasNoBags))]
    public partial LoadState BagsState { get; set; } = LoadState.Ready;

    public bool ShowBagsSkeleton => BagsState is LoadState.Loading or LoadState.Waking;

    public bool HasBagsError => BagsState == LoadState.Error;

    public bool HasBags => BagsState == LoadState.Ready && Bags.Count > 0;

    public bool HasNoBags => BagsState == LoadState.Ready && Bags.Count == 0 && SelectedTile is not null;

    public string BagsErrorText => "Can't load the bags. Check your connection, then retry.";

    /// <summary>"Showing the oldest 200 of 412 bags." when the list is capped, otherwise empty.</summary>
    [ObservableProperty]
    public partial string BagsMoreText { get; set; } = string.Empty;

    internal TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public AsyncRelayCommand RetryBagsCommand => field ??= Command(_ => LoadBagsAsync(SelectedTile));

    partial void OnSelectedTileChanged(StockTile? oldValue, StockTile? newValue)
    {
        if (oldValue?.BloodType != newValue?.BloodType)
        {
            _ = LoadBagsAsync(newValue);
        }
    }

    private async Task LoadBagsAsync(StockTile? tile)
    {
        _bagsLoad?.Cancel();
        _bagsLoad?.Dispose();
        _bagsLoad = null;
        Bags.Clear();
        BagsMoreText = string.Empty;
        if (tile is null || tile.Available == 0)
        {
            BagsState = LoadState.Ready;
            RaiseBags();
            return;
        }

        var load = new CancellationTokenSource();
        _bagsLoad = load;
        BagsState = LoadState.Loading;
        try
        {
            var result = await RunOperationAsync(ct => _api.GetStockBagsAsync(tile.BloodType, ct), load.Token).ConfigureAwait(true);
            if (load.IsCancellationRequested)
            {
                return;
            }

            if (!result.IsOk)
            {
                BagsState = LoadState.Error;
                return;
            }

            var today = DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);
            var position = 0;
            foreach (var bag in result.Value.Bags)
            {
                Bags.Add(new StockBagRow(bag, ++position, today, Zone));
            }

            BagsMoreText = result.Value.HasMore ? $"Showing the oldest {Bags.Count:N0} of {tile.Available:N0} bags." : string.Empty;
            BagsState = LoadState.Ready;
            RaiseBags();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            // Another group was chosen or the panel closed; that load owns the list now.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bagsLoad?.Cancel();
            _bagsLoad?.Dispose();
            _bagsLoad = null;
        }

        base.Dispose(disposing);
    }

    private void RaiseBags()
    {
        OnPropertyChanged(nameof(HasBags));
        OnPropertyChanged(nameof(HasNoBags));
    }

    public string? PanelOverline => SelectedTile is { } tile ? $"Blood group {tile.GroupText}" : null;

    /// <summary>True when the panel can open the group's donors and requests.</summary>
    public bool CanNavigate => _navigation is not null;

    public AsyncRelayCommand ClosePanelCommand => field ??= Command(_ =>
    {
        SelectedTile = null;
        return Task.CompletedTask;
    });

    /// <summary>The donors of the selected group (to call in for a drive).</summary>
    public AsyncRelayCommand FindDonorsCommand => field ??= Command(async _ =>
    {
        if (SelectedTile is { } tile && _navigation is not null)
        {
            await _navigation.NavigateTo<DonorsViewModel>(new ListPrefilter(BloodType: tile.BloodType)).ConfigureAwait(true);
        }
    });

    /// <summary>The requests that ask for the selected group.</summary>
    public AsyncRelayCommand OpenRequestsCommand => field ??= Command(async _ =>
    {
        if (SelectedTile is { } tile && _navigation is not null)
        {
            await _navigation.NavigateTo<RequestsViewModel>(new ListPrefilter(BloodType: tile.BloodType)).ConfigureAwait(true);
        }
    });

    public string SummaryText => LowCount == 0
        ? "Every blood type is above its threshold."
        : LowCount == 1 ? "1 blood type is below its threshold." : $"{LowCount} blood types are below their thresholds.";

    protected override bool HasNoData => _fetched.Count == 0;

    public void ReceiveParameter(object parameter)
    {
        if (parameter is not ListPrefilter prefilter)
        {
            return;
        }

        _restored = true; // the request of another screen wins over what was remembered
        BloodTypeFilter = BloodTypeOptions.First(o => o.Value == prefilter.BloodType);
        ViewFilter = ViewOptions.First(o => o.Value == prefilter.StockView);
        _state.Filters["bloodType"] = prefilter.BloodType?.ToDisplay();
        _state.Filters["view"] = prefilter.StockView?.ToString();
    }

    partial void OnBloodTypeFilterChanged(FilterOption<BloodType> value)
    {
        _state.Filters["bloodType"] = value.Value?.ToDisplay();
        OnPropertyChanged(nameof(BloodTypeChipText));
        OnPropertyChanged(nameof(HasBloodTypeFilter));
        ApplyFilters();
    }

    partial void OnViewFilterChanged(FilterOption<StockFilter> value)
    {
        _state.Filters["view"] = value.Value?.ToString();
        OnPropertyChanged(nameof(IsAllView));
        OnPropertyChanged(nameof(IsLowView));
        OnPropertyChanged(nameof(IsExpiringView));
        ApplyFilters();
    }

    /// <summary>Back to every blood type and all stock.</summary>
    public AsyncRelayCommand ClearFiltersCommand => field ??= Command(_ =>
    {
        BloodTypeFilter = BloodTypeOptions[0];
        ViewFilter = ViewOptions[0];
        return Task.CompletedTask;
    });

    public AsyncRelayCommand RetryCommand => field ??= Command(ct => LoadAsync(ct));

    protected override async Task FetchAsync(CancellationToken ct)
    {
        _fetched = (await _api.GetStockAsync(ct).ConfigureAwait(false)).Unwrap();
    }

    protected override void Apply()
    {
        RestoreRememberedFilters();
        LowCount = _fetched.Count(i => i.BelowThreshold);
        ExpiringCount = _fetched.Count(i => i.ExpiringSoon > 0);
        GroupCount = _fetched.Count;
        TotalUnits = _fetched.Sum(i => i.Available);
        TotalExpiring = _fetched.Sum(i => i.ExpiringSoon);
        var thresholds = _fetched.Select(i => i.Threshold).Distinct().ToList();
        MinimumText = thresholds.Count switch
        {
            0 => string.Empty,
            1 => $"Minimum {thresholds[0]:N0} units per group",
            _ => "Minimums differ by group",
        };
        var low = _fetched.Where(i => i.BelowThreshold).ToList();
        var outOf = low.Where(i => i.Available == 0).Select(i => i.BloodType.ToTypographic()).ToList();
        ShortageText = low.Count == 0
            ? string.Empty
            : $"{low.Count} of {_fetched.Count} blood groups are below the minimum." + (outOf.Count == 0 ? string.Empty : $" {string.Join(" and ", outOf)} {(outOf.Count == 1 ? "is" : "are")} out of stock.");
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasShortage));
        OnPropertyChanged(nameof(HasStock));
        var open = SelectedTile?.BloodType;
        ApplyFilters();
        if (open is not null && SelectedTile?.BloodType == open)
        {
            _ = LoadBagsAsync(SelectedTile); // a refresh: the open group's bags may have changed too
        }
    }

    /// <summary>True once the stock has at least one group: the tabs, the legend and the table show only then.</summary>
    public bool HasStock => _fetched.Count > 0;

    /// <summary>The empty state's action. Donations are recorded from an appointment, so it opens Appointments (null without navigation).</summary>
    public AsyncRelayCommand? RecordDonationCommand => _navigation is null ? null : field ??= Command(async _ =>
    {
        await _navigation.NavigateTo<Appointments.AppointmentsViewModel>().ConfigureAwait(true);
    });

    private bool _restored;

    private void RestoreRememberedFilters()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        if (BloodTypeFilter.Value is null && _state.Filters.TryGetValue("bloodType", out var type) && BloodTypeExtensions.TryParseDisplay(type, out var parsed))
        {
            BloodTypeFilter = BloodTypeOptions.First(o => o.Value == parsed);
        }

        if (ViewFilter.Value is null && _state.Filters.TryGetValue("view", out var view) && Enum.TryParse<StockFilter>(view, out var filter) && filter != StockFilter.All)
        {
            ViewFilter = ViewOptions.First(o => o.Value == filter);
        }
    }

    private void ApplyFilters()
    {
        var type = BloodTypeFilter.Value;
        var view = ViewFilter.Value;
        // One scale for every row, so the bars compare: three times the largest minimum. The minimum tick sits at a third of the bar whatever the
        // largest stock is, so "low" reads at a glance; a group with more fills the bar, and its number says how much.
        var scale = _fetched.Count == 0 ? 0 : _fetched.Max(i => i.Threshold) * 3.0;
        var selected = SelectedTile?.BloodType;
        var visible = _fetched
            .Where(i => type is null || i.BloodType == type)
            .Where(i => view switch
            {
                StockFilter.BelowThreshold => i.BelowThreshold,
                StockFilter.ExpiringSoon => i.ExpiringSoon > 0,
                _ => true,
            })
            .Select(i => StockTile.From(i, scale))
            .ToList();
        Tiles.Clear();
        foreach (var tile in visible)
        {
            Tiles.Add(tile);
        }

        // The panel stays on its group while that group is still listed.
        SelectedTile = selected is { } keep ? Tiles.FirstOrDefault(t => t.BloodType == keep) : null;

        NothingMatches = _fetched.Count > 0 && Tiles.Count == 0;
    }
}
