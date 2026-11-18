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
/// The "Fulfil request" dialog, opened from a request (FEAT-16). It shows what will be issued and how much of that blood is in stock; the
/// oldest unexpired bags are issued by the API in one step. When the stock is not enough the API changes nothing, and the dialog says so in plain
/// words and stays open. A successful call issues the receipt, available as <see cref="Receipt"/>.
/// </summary>
public sealed partial class FulfilRequestDialogViewModel : DialogViewModel
{
    private readonly ILifestreamApiClient _api;
    private readonly RequestDetail _request;

    public FulfilRequestDialogViewModel(ILifestreamApiClient api, RequestDetail request)
        : base("Fulfil request", "Fulfil request", "Issuing...")
    {
        _api = api;
        _request = request;
        PendingStock = LoadStockAsync();
    }

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

    private async Task LoadStockAsync()
    {
        var result = await Task.Run(() => _api.GetStockAsync()).ConfigureAwait(true);
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
            return;
        }

        base.ShowProblem(problem);
    }
}
