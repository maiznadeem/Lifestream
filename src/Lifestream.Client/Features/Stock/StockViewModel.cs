using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
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

    internal static StockTile From(StockItem item) => new(item.BloodType, item.Available, item.ExpiringSoon, item.Threshold, item.BelowThreshold);
}

/// <summary>
/// The Stock screen (FEAT-19): one tile per blood type from a single call. Its filters work on the tiles already loaded, so they apply the
/// moment they change, with no extra call.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML, which needs instance properties.")]
public sealed partial class StockViewModel(ILifestreamApiClient api, IListViewStateStore states)
    : ViewModelBase, INavigationParameterReceiver
{
    private readonly ListViewState _state = states.GetState("stock");
    private IReadOnlyList<StockItem> _fetched = [];

    public override string PageTitle => "Stock";

    private static readonly IReadOnlyList<FilterOption<BloodType>> BloodTypeChoices = FilterOptions.WithAll(BloodTypeExtensions.All, b => b.ToDisplay(), "All blood types");

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
        ApplyFilters();
    }

    partial void OnViewFilterChanged(FilterOption<StockFilter> value)
    {
        _state.Filters["view"] = value.Value?.ToString();
        ApplyFilters();
    }

    /// <summary>Back to every blood type and all stock.</summary>
    public AsyncRelayCommand ClearFiltersCommand => field ??= new AsyncRelayCommand(_ =>
    {
        BloodTypeFilter = BloodTypeOptions[0];
        ViewFilter = ViewOptions[0];
        return Task.CompletedTask;
    });

    public AsyncRelayCommand RetryCommand => field ??= new AsyncRelayCommand(ct => LoadAsync(ct));

    protected override async Task FetchAsync(CancellationToken ct)
    {
        _fetched = (await api.GetStockAsync(ct).ConfigureAwait(false)).Unwrap();
    }

    protected override void Apply()
    {
        RestoreRememberedFilters();
        LowCount = _fetched.Count(i => i.BelowThreshold);
        ExpiringCount = _fetched.Count(i => i.ExpiringSoon > 0);
        OnPropertyChanged(nameof(SummaryText));
        ApplyFilters();
    }

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
        var visible = _fetched
            .Where(i => type is null || i.BloodType == type)
            .Where(i => view switch
            {
                StockFilter.BelowThreshold => i.BelowThreshold,
                StockFilter.ExpiringSoon => i.ExpiringSoon > 0,
                _ => true,
            })
            .Select(StockTile.From)
            .ToList();
        Tiles.Clear();
        foreach (var tile in visible)
        {
            Tiles.Add(tile);
        }

        NothingMatches = _fetched.Count > 0 && Tiles.Count == 0;
    }
}
