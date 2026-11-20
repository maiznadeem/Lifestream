using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Requests;

/// <summary>
/// The "Fulfil request" step, opened from a request (FEAT-16). It shows what will be issued, the bags the API will take (the oldest unexpired bags of the group, in
/// that order, with their codes, collection and expiry dates, audit R3) and how much of that blood is in stock; the API issues them in one step. The API issues a
/// request in full or not at all: while the stock is known to be short the step says how many units are missing, lists what is in stock (not as "bags to issue")
/// and does not offer "Fulfil request", which the server would refuse. A successful call issues the receipt, available as <see cref="Receipt"/>. "Not now" leaves
/// the request as it is.
/// </summary>
[NoUserInput("It shows what will be issued and asks to confirm; there is no field to type in.")]
public sealed partial class FulfilRequestDialogViewModel : DialogViewModel
{
    private readonly ILifestreamApiClient _api;
    private readonly RequestDetail _request;

    public FulfilRequestDialogViewModel(ILifestreamApiClient api, RequestDetail request)
        : base("Fulfil request", "Fulfil request", "Issuing...", "Not now")
    {
        _api = api;
        _request = request;
        PendingStock = LoadStockAsync();
        PendingBags = LoadBagsAsync();
    }

    /// <summary>Completes when the list of bags to issue has loaded (for tests).</summary>
    internal Task PendingBags { get; private set; }

    /// <summary>The bags the fulfil will issue, oldest first. Empty until known (or when the list could not be loaded).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SuggestedBagRow> Bags { get; set; } = [];

    /// <summary>A line above or instead of the bags: what the list is, that it could not be loaded, or that there are too few to issue.</summary>
    [ObservableProperty]
    public partial string? BagsNote { get; set; }

    /// <summary>What the list of bags is, for a screen reader: the bags the fulfil will issue, or (when short) only what is in stock.</summary>
    public string BagsLabel => StockIsShort ? "Bags in stock" : "Bags to issue";

    /// <summary>
    /// While the stock is known to be short: how many units are missing and that nothing can be issued yet ("Only 2 of the 3 units of O− needed are in stock,
    /// 1 unit short. A request is issued in full, so it stays open until more arrives."). Null when the stock covers the request or is unknown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShortReason))]
    public partial string? ShortReason { get; set; }

    public bool HasShortReason => ShortReason is not null;

    /// <summary>"Fulfil request" is offered only while the server will accept it: not while the stock is known to be short.</summary>
    protected override bool CanRunPrimary => !StockIsShort;

    partial void OnStockIsShortChanged(bool value)
    {
        OnPropertyChanged(nameof(BagsLabel));
        RaisePrimaryChanged();
    }

    /// <summary>True once the bags to issue have answered: their count is the server's own, so a stock list that answers later does not replace it.</summary>
    private bool _bagsCounted;

    private void ShowAvailable(int available)
    {
        StockText = $"In stock: {RecordFormatting.Units(available)} of {BloodTypeText}";
        var cover = RequestCoverage.Of(_request.Units, available);
        ShortReason = cover.HoldReason(_request.BloodType);
        StockIsShort = cover.Kind != CoverKind.CanFulfil;
    }

    private async Task LoadBagsAsync()
    {
        var result = await RunOperationAsync(ct => _api.GetFulfilPreviewAsync(_request.Id, ct)).ConfigureAwait(true);
        if (!result.IsOk)
        {
            BagsNote = "The bags couldn't be listed. The oldest bags that have not expired are issued.";
            return;
        }

        var preview = result.Value;
        var zone = Clock.LocalTimeZone;
        Bags = [.. preview.Bags.Select((b, i) => new SuggestedBagRow(
            i + 1,
            Donors.DonationHistory.BagCode(b.Id),
            $"Collected {Formatting.Date(RecordFormatting.LocalDate(b.CollectedAtUtc, zone))}" + (b.DonorName is { Length: > 0 } donor ? $" from {donor}" : string.Empty),
            "Use by " + Formatting.Date(b.ExpiresOn)))];
        // The same query the fulfil runs counted these: this is what the server will find, so the step follows it (the stock list may be a moment older).
        _bagsCounted = true;
        ShowAvailable(preview.Available);
        BagsNote = preview.Bags.Count == 0 ? $"There is no {BloodTypeText} blood in stock that has not expired."
            : StockIsShort ? "Not enough to issue. In stock now, oldest first:"
            : "These bags are issued, oldest first:";
    }

    /// <summary>The request being fulfilled.</summary>
    public int RequestId => _request.Id;

    public string PatientName => _request.PatientName;

    /// <summary>The request's group as every badge writes it, with a true minus sign ("O−").</summary>
    public string BloodTypeText => _request.BloodType.ToTypographic();

    public string UnitsText => RecordFormatting.Units(_request.Units);

    public string RequiredByText => Formatting.Date(_request.RequiredBy);

    /// <summary>The receipt the API issued; set after a successful call.</summary>
    public ReceiptDetail? Receipt { get; private set; }

    /// <summary>Completes when the stock check started when the dialog opened has finished (for tests).</summary>
    internal Task PendingStock { get; private set; }

    /// <summary>"In stock: 12 units of A+", empty until known.</summary>
    [ObservableProperty]
    public partial string StockText { get; set; } = "Checking stock...";

    /// <summary>True when the stock is known to be smaller than the request: shown as a warning, and "Fulfil request" is not offered (the server would refuse it).</summary>
    [ObservableProperty]
    public partial bool StockIsShort { get; set; }

    protected override string RecordType => "request";

    /// <summary>The dialog has no inputs (it shows what will be issued and asks to confirm), so there is nothing to lose by closing it.</summary>
    public override bool HasUnsavedChanges => false;

    private async Task LoadStockAsync()
    {
        var result = await RunOperationAsync(ct => _api.GetStockAsync(ct)).ConfigureAwait(true);
        if (!result.IsOk)
        {
            if (!_bagsCounted)
            {
                StockText = "Stock couldn't be checked. The server checks it when you fulfil.";
            }

            return;
        }

        if (!_bagsCounted)
        {
            // The bags to issue have not answered yet (or could not be listed): the stock list is the figure until they do.
            ShowAvailable(result.Value.FirstOrDefault(s => s.BloodType == _request.BloodType)?.Available ?? 0);
        }
    }

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareSubmit()
    {
        var id = _request.Id;
        return async ct =>
        {
            var result = await _api.FulfilRequestAsync(id, ct).ConfigureAwait(false);
            if (!result.IsOk)
            {
                return result.Problem;
            }

            Receipt = result.Value;
            return null;
        };
    }

    protected override void ShowProblem(ApiProblem problem)
    {
        if (problem.Code == ProblemCodes.InsufficientStock)
        {
            // The stock went down after the step opened (another fulfil, a bag expired or was discarded). Nothing was issued; read what is there now, which says
            // how many units are short and takes "Fulfil request" away.
            FormError = $"Nothing was issued: there is no longer enough {BloodTypeText} in stock for the {RecordFormatting.Units(_request.Units)} this request needs.";
            StockIsShort = true;
            _bagsCounted = false;
            PendingStock = LoadStockAsync(); // show the current figure
            PendingBags = LoadBagsAsync(); // and the bags there are now
            return;
        }

        base.ShowProblem(problem);
    }
}
