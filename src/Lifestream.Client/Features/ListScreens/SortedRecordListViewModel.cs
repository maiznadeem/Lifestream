using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Contracts;

namespace Lifestream.Client.Features.ListScreens;

/// <summary>
/// A list screen that only ever sends sort keys the API supports. Each screen names its <see cref="SortKeys"/> (the columns that offer sorting, and only
/// those); anything else a remembered or requested sort holds is dropped before the call. If the API still answers a sorted request with a validation
/// problem, the screen goes back to the default sort and asks again, so a bad sort is never remembered and never leaves the screen in an error.
/// </summary>
public abstract class SortedRecordListViewModel<TItem, TDetail> : RecordListViewModel<TItem, TDetail>
    where TItem : class
    where TDetail : class
{
    private bool _sortWasReset;

    protected SortedRecordListViewModel(IListViewStateStore states, string stateKey, IToastService toasts, IDialogService dialogs, IUiDispatcher ui)
        : base(states, stateKey, toasts, dialogs, ui)
    {
    }

    /// <summary>The sort keys this screen may send, without a leading "-" (descending). Empty when no column of the screen sorts.</summary>
    public abstract IReadOnlyList<string> SortKeys { get; }

    /// <summary>Calls the list endpoint with the screen's own filters; <see cref="ListRequest.Sort"/> is null or a supported key.</summary>
    protected abstract Task<ApiResult<PagedResult<TItem>>> FetchSortedAsync(ListRequest request, CancellationToken ct);

    protected sealed override async Task<ApiResult<PagedResult<TItem>>> FetchPageAsync(ListRequest request, CancellationToken ct)
    {
        if (request.Sort is { } sort && !SortKeys.Contains(sort.TrimStart('-', '+'), StringComparer.OrdinalIgnoreCase))
        {
            ResetSort(); // a remembered sort that this screen can't send
            request = request with { Sort = null };
        }

        var result = await FetchSortedAsync(request, ct).ConfigureAwait(false);
        if (!result.IsOk && request.Sort is not null && result.Problem!.Code == ProblemCodes.Validation)
        {
            ResetSort();
            result = await FetchSortedAsync(request with { Sort = null }, ct).ConfigureAwait(false);
        }

        return result;
    }

    private void ResetSort()
    {
        ViewState.SortColumn = null;
        ViewState.SortDescending = false;
        _sortWasReset = true;
    }

    protected override void Apply()
    {
        base.Apply();
        if (_sortWasReset)
        {
            _sortWasReset = false;
            OnPropertyChanged(nameof(SortColumn));
            OnPropertyChanged(nameof(SortDescending));
        }
    }
}
