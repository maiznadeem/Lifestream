using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Contracts;

namespace Lifestream.Client.Features.ListScreens;

/// <summary>Numbers every list screen shares.</summary>
public static class ListDefaults
{
    /// <summary>How long typing must pause before the search is sent (FEAT-02).</summary>
    public static TimeSpan SearchDebounce { get; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Rows per page (FEAT-01); the API caps it at 200.</summary>
    public const int PageSize = Paging.DefaultPageSize;
}

/// <summary>Another tab of a list screen where the current search and filters find something: its label, how many, and how to go there.</summary>
public sealed record OtherViewMatch(string Tab, int Count, Action Show);

/// <summary>What the next fetch asks the API for.</summary>
public readonly record struct ListRequest(int Page, int PageSize, string? Search, string? Sort);

/// <summary>
/// Base of every list screen (FEAT-01, FEAT-02, FEAT-03). One page of at most 50 rows with a "Showing X-Y of N" count, further pages
/// fetched from the API, a search that is sent 300 ms after typing stops (or at once on Enter) and never sent when empty, "Clear
/// filters", server-side sort, and the search, filters and sort kept for the session in <see cref="ListViewState"/>. The skeleton, empty
/// and error states come from <see cref="ViewModelBase"/>: every fetch goes through <see cref="ViewModelBase.LoadAsync"/>.
/// </summary>
public abstract partial class PagedListViewModel<TItem> : ViewModelBase
{
    private readonly IUiDispatcher _ui;
    private ITimer? _debounce;
    private PagedResult<TItem>? _fetched;
    private bool _restoring;

    protected PagedListViewModel(IListViewStateStore states, string stateKey, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(ui);
        _ui = ui;
        ViewState = states.GetState(stateKey);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(State))
            {
                OnPropertyChanged(nameof(HeaderCount));
            }
        };
        _restoring = true;
        SearchText = ViewState.Search ?? string.Empty;
        _restoring = false;

        SearchNowCommand = Command(_ =>
        {
            CancelDebounce();
            Page = 1;
            return RefreshAsync();
        });
        ClearFiltersCommand = Command(_ =>
        {
            CancelDebounce();
            _restoring = true;
            SearchText = string.Empty;
            ResetExtraFilters();
            _restoring = false;
            ViewState.Search = null;
            ViewState.Filters.Clear();
            Page = 1;
            OnFiltersChanged();
            return RefreshAsync();
        });
        NextPageCommand = Command(_ => GoToPageAsync(Page + 1), () => HasNextPage);
        PreviousPageCommand = Command(_ => GoToPageAsync(Page - 1), () => HasPreviousPage);
        SortCommand = Command((expression, _) =>
        {
            ApplySort(expression as string);
            Page = 1;
            return RefreshAsync();
        });
        RetryCommand = Command(_ => RefreshAsync());
    }

    /// <summary>The rows of the current page.</summary>
    public ObservableCollection<TItem> Items { get; } = [];

    /// <summary>The session's search, filters, sort and scroll position for this screen (SHELL-24).</summary>
    public ListViewState ViewState { get; }

    public AsyncRelayCommand SearchNowCommand { get; }

    public AsyncRelayCommand ClearFiltersCommand { get; }

    public AsyncRelayCommand NextPageCommand { get; }

    public AsyncRelayCommand PreviousPageCommand { get; }

    public AsyncRelayCommand SortCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    /// <summary>The sorted column's server name (for example "name"), or null; the grid shows its indicator from this (SHELL-39).</summary>
    public string? SortColumn => ViewState.SortColumn;

    public bool SortDescending => ViewState.SortDescending;

    /// <summary>What is in the search box. Typing sends the search after <see cref="ListDefaults.SearchDebounce"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters), nameof(EmptyTitle), nameof(EmptyMessage), nameof(EmptyActionText), nameof(EmptyActionCommand))]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeText), nameof(HasPreviousPage), nameof(HasNextPage), nameof(PageCountText))]
    public partial int Page { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeText), nameof(HasNextPage), nameof(PageCountText), nameof(HeaderCount))]
    public partial int Total { get; set; }

    /// <summary>The page header's count: the total once the list has loaded (or is empty), and nothing while it loads or after it failed, when a number would be false.</summary>
    public string? HeaderCount => State is LoadState.Ready or LoadState.Empty ? Total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : null;

    /// <summary>The search text to send: null when the box is empty or only spaces, so an empty search never filters (FEAT-02).</summary>
    public string? EffectiveSearch => string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();

    /// <summary>"Showing 1–50 of 1,230" (FEAT-01), with an en dash.</summary>
    public string RangeText
    {
        get
        {
            if (Total == 0 || Items.Count == 0)
            {
                return "Showing 0 of 0";
            }

            var first = ((Page - 1) * ListDefaults.PageSize) + 1;
            return $"Showing {first:N0}\u2013{first + Items.Count - 1:N0} of {Total:N0}";
        }
    }

    /// <summary>"Page 1 of 3": shown whenever the list has rows, also when there is only one page (every list shows the same footer), empty when it has none.</summary>
    public string PageCountText => Total == 0 ? string.Empty : $"Page {Page} of {LastPage}";

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < LastPage;

    private int LastPage => Math.Max(1, (int)Math.Ceiling(Total / (double)ListDefaults.PageSize));

    /// <summary>True when a search or any filter narrows the list; "Clear filters" is offered then.</summary>
    public bool HasActiveFilters => EffectiveSearch is not null || HasExtraFilters;

    /// <summary>Filters other than the search box that are not at "All".</summary>
    protected virtual bool HasExtraFilters => false;

    /// <summary>Sets the extra filters back to "All" (called by "Clear filters"; do not refresh from here).</summary>
    protected virtual void ResetExtraFilters()
    {
    }

    /// <summary>Plural name used in the empty state, such as "donors".</summary>
    protected abstract string PluralName { get; }

    /// <summary>
    /// The empty state's title. With a search or filter on a screen with tabs, an empty tab is not "nothing matches" when another tab holds the matches:
    /// it then names the tab and what was looked for ("No appointments today match “eastwood”"), and the message and the action lead to the others.
    /// </summary>
    public string EmptyTitle => !HasActiveFilters ? EmptyNoFilterTitle
        : MatchesInOtherViews.Count == 0 ? $"No {PluralName} match these filters"
        : $"{EmptyInThisViewTitle} {(EffectiveSearch is { } search ? $"“{search}”" : "these filters")}";

    /// <summary>The empty-state title when nothing is filtered ("No donors yet"); a screen with saved views names the view ("No cancelled requests").</summary>
    protected virtual string EmptyNoFilterTitle => $"No {PluralName} yet";

    /// <summary>The start of the title when this tab has no match and another has, up to the word "match" ("No appointments today match").</summary>
    protected virtual string EmptyInThisViewTitle => $"No {PluralName} on this tab match";

    /// <summary>
    /// On a screen with tabs: the other tabs where the same search and filters find something, the one with the most matches first (the tabs' own
    /// order among equals). Empty on a screen without tabs, when no other tab has a match, and while the tabs' counts are not known.
    /// </summary>
    protected virtual IReadOnlyList<OtherViewMatch> MatchesInOtherViews => [];

    public string EmptyMessage => !HasActiveFilters ? EmptyAddMessage
        : MatchesInOtherViews is { Count: > 0 } elsewhere ? string.Join(", ", elsewhere.Select(m => $"{m.Count:N0} in {m.Tab}")) + "."
        : "Try a different search, or clear the filters to see everyone.";

    /// <summary>The empty-state message when nothing is filtered.</summary>
    protected virtual string EmptyAddMessage => $"There are no {PluralName} to show.";

    public string? EmptyActionText => !HasActiveFilters ? NewActionText : MatchesInOtherViews is [var first, ..] ? $"Show {first.Tab}" : "Clear filters";

    public System.Windows.Input.ICommand? EmptyActionCommand => !HasActiveFilters ? NewCommand : MatchesInOtherViews.Count > 0 ? ShowMatchingViewCommand : ClearFiltersCommand;

    /// <summary>Goes to the other tab that holds the most matches of the search and filters (the empty state's action when this tab has none).</summary>
    public AsyncRelayCommand ShowMatchingViewCommand => field ??= Command(_ =>
    {
        if (MatchesInOtherViews is [var first, ..])
        {
            first.Show();
        }

        return Task.CompletedTask;
    });

    /// <summary>The text of the "add" action of the empty state; null when the screen has none.</summary>
    protected virtual string? NewActionText => null;

    protected override bool HasNoData => _fetched is { Total: 0 };

    /// <summary>Completes when the refresh that started last has finished (for tests).</summary>
    internal Task PendingRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Calls the list endpoint with the screen's own filters.</summary>
    [RunsInOperation("FetchAsync calls it, and LoadAsync runs FetchAsync inside Operations.RunAsync.")]
    protected abstract Task<ApiResult<PagedResult<TItem>>> FetchPageAsync(ListRequest request, CancellationToken ct);

    /// <summary>Call after a filter other than the search box changed: the filter applies immediately (FEAT-02).</summary>
    protected void FilterChanged()
    {
        if (_restoring)
        {
            return;
        }

        CancelDebounce();
        Page = 1;
        OnFiltersChanged();
        _ = RefreshAsync();
    }

    /// <summary>Puts text in the search box without sending it (the first load that follows sends it).</summary>
    protected void SetSearchWithoutRefresh(string text)
    {
        _restoring = true;
        SearchText = text;
        _restoring = false;
        ViewState.Search = EffectiveSearch;
    }

    protected void OnFiltersChanged() => Raise(nameof(HasActiveFilters), nameof(EmptyTitle), nameof(EmptyMessage), nameof(EmptyActionText), nameof(EmptyActionCommand));

    /// <summary>Reloads the page with the current search, filters, sort and page; the skeleton shows while it runs.</summary>
    public Task RefreshAsync()
    {
        PendingRefresh = LoadAsync();
        return PendingRefresh;
    }

    protected sealed override async Task FetchAsync(CancellationToken ct)
    {
        var request = new ListRequest(Page, ListDefaults.PageSize, EffectiveSearch, ViewState.SortExpression);
        var result = (await FetchPageAsync(request, ct).ConfigureAwait(false)).Unwrap();
        if (result.Items.Count == 0 && result.Total > 0 && request.Page > 1)
        {
            // The page we asked for no longer exists (the last row on it was deleted): show the last page that does.
            var last = Math.Max(1, (int)Math.Ceiling(result.Total / (double)ListDefaults.PageSize));
            result = (await FetchPageAsync(request with { Page = last }, ct).ConfigureAwait(false)).Unwrap();
        }

        _fetched = result;
    }

    protected override void Apply()
    {
        if (_fetched is not { } page)
        {
            return;
        }

        Items.Clear();
        foreach (var item in page.Items)
        {
            Items.Add(item);
        }

        Page = page.Page;
        Total = page.Total;
        OnPropertyChanged(nameof(RangeText));
        NextPageCommand.RaiseCanExecuteChanged();
        PreviousPageCommand.RaiseCanExecuteChanged();
    }

    private Task GoToPageAsync(int page)
    {
        Page = Math.Clamp(page, 1, LastPage);
        return RefreshAsync();
    }

    private void ApplySort(string? expression)
    {
        if (string.IsNullOrEmpty(expression))
        {
            ViewState.SortColumn = null;
            ViewState.SortDescending = false;
        }
        else
        {
            ViewState.SortDescending = expression[0] == '-';
            ViewState.SortColumn = ViewState.SortDescending ? expression[1..] : expression;
        }

        Raise(nameof(SortColumn), nameof(SortDescending));
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_restoring)
        {
            return;
        }

        ViewState.Search = EffectiveSearch;
        CancelDebounce();
        _debounce = Clock.CreateTimer(_ => PostToUi(OnDebounceElapsed), null, ListDefaults.SearchDebounce, Timeout.InfiniteTimeSpan);
    }

    private void OnDebounceElapsed()
    {
        _debounce?.Dispose();
        _debounce = null;
        Page = 1;
        _ = RefreshAsync();
    }

    private void CancelDebounce()
    {
        _debounce?.Dispose();
        _debounce = null;
    }

    /// <summary>Runs the action on the UI thread (inline when already there): timers fire on other threads.</summary>
    protected void PostToUi(Action action) => _ui.Run(action);

    private void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            OnPropertyChanged(name);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelDebounce();
        }

        base.Dispose(disposing);
    }
}
