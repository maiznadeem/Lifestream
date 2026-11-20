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
/// that order, with their codes, collection and expiry dates, audit R3) and how much of that blood is in stock; the API issues them in one step. When the stock is not
/// enough the API changes nothing, and the step says so in plain words and stays open. A successful call issues the receipt, available as <see cref="Receipt"/>.
/// "Not now" leaves the request as it is.
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

    /// <summary>A line above or instead of the bags: what the list is, that it could not be loaded, or how many are missing.</summary>
    [ObservableProperty]
    public partial string? BagsNote { get; set; }

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
        var missing = _request.Units - preview.Bags.Count;
        BagsNote = preview.Bags.Count == 0 ? $"There is no {BloodTypeText} blood in stock that has not expired."
            : missing > 0 ? $"Only {RecordFormatting.Units(preview.Bags.Count)} can be issued; {RecordFormatting.Units(missing)} short."
            : "These bags are issued, oldest first:";
    }

    /// <summary>The request being fulfilled.</summary>
    public int RequestId => _request.Id;

    public string PatientName => _request.PatientName;

    public string BloodTypeText => _request.BloodType.ToDisplay();

    public string UnitsText => RecordFormatting.Units(_request.Units);

    public string RequiredByText => Formatting.Date(_request.RequiredBy);

    /// <summary>The receipt the API issued; set after a successful call.</summary>
    public ReceiptDetail? Receipt { get; private set; }

    /// <summary>Completes when the stock check started when the dialog opened has finished (for tests).</summary>
    internal Task PendingStock { get; private set; }

    /// <summary>"In stock: 12 units of A+", empty until known.</summary>
    [ObservableProperty]
    public partial string StockText { get; set; } = "Checking stock...";

    /// <summary>True when the stock is known to be smaller than the request: shown as a warning, but the API still decides.</summary>
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
            StockText = "Stock couldn't be checked. The server checks it when you fulfil.";
            return;
        }

        var available = result.Value.FirstOrDefault(s => s.BloodType == _request.BloodType)?.Available ?? 0;
        ShowStock(available);
    }

    private void ShowStock(int available)
    {
        StockText = $"In stock: {RecordFormatting.Units(available)} of {BloodTypeText}";
        StockIsShort = available < _request.Units;
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
            FormError = $"There isn't enough {BloodTypeText} blood in stock: this request needs {RecordFormatting.Units(_request.Units)}. Nothing was issued.";
            StockIsShort = true;
            PendingStock = LoadStockAsync(); // show the current figure
            PendingBags = LoadBagsAsync(); // and the bags there are now
            return;
        }

        base.ShowProblem(problem);
    }
}
