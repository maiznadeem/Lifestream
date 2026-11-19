using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Contracts.Receipts;

namespace Lifestream.Client.Features.Receipts;

/// <summary>Opens a receipt in its printable view. Used after an issue and from the receipts list; a receipt can be opened and printed as often as needed.</summary>
public interface IReceiptViewer
{
    /// <param name="receiptId">The receipt to show.</param>
    /// <param name="isDuplicate">True when it is opened again later, so a printed copy says "Duplicate copy".</param>
    Task ShowAsync(int receiptId, bool isDuplicate);
}

public sealed class ReceiptViewerService(ILifestreamApiClient api, IReceiptPrinter printer, IFormDialogService dialogs) : IReceiptViewer
{
    public async Task ShowAsync(int receiptId, bool isDuplicate)
    {
        using var viewer = new ReceiptViewerViewModel(api, printer, receiptId, isDuplicate);
        _ = viewer.LoadAsync(); // shows the skeleton at once; the dialog fills in when the receipt arrives
        await dialogs.ShowAsync(viewer).ConfigureAwait(true);
    }
}

/// <summary>
/// The printable receipt (FEAT-18): the receipt's lines with Print and Save as PDF actions. Print and Save as PDF can be used any number of times,
/// so a duplicate receipt can always be produced; failures and "no printer" are said in words, and a cancelled print dialog is not an error.
/// </summary>
public sealed partial class ReceiptViewerViewModel : DialogViewModel
{
    private readonly ILifestreamApiClient _api;
    private readonly IReceiptPrinter _printer;
    private readonly bool _isDuplicate;
    private ReceiptDetail? _fetched;

    public ReceiptViewerViewModel(ILifestreamApiClient api, IReceiptPrinter printer, int receiptId, bool isDuplicate)
        : base("Receipt", null, cancelText: "Close")
    {
        _api = api;
        _printer = printer;
        _isDuplicate = isDuplicate;
        ReceiptId = receiptId;
        State = LoadState.Loading;
        PrintCommand = new AsyncRelayCommand(_ => SendAsync(save: false), () => Document is not null && !IsBusy);
        SavePdfCommand = new AsyncRelayCommand(_ => SendAsync(save: true), () => Document is not null && !IsBusy);
        RetryCommand = new AsyncRelayCommand(ct => LoadAsync(ct));
    }

    public int ReceiptId { get; }

    public override string PageTitle => "Receipt";

    public AsyncRelayCommand PrintCommand { get; }

    public AsyncRelayCommand SavePdfCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    protected override string RecordType => "receipt";

    [ObservableProperty]
    public partial ReceiptDocument? Document { get; set; }

    /// <summary>"Sent to the printer." after a print; says what happened without a dialog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; set; }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    partial void OnDocumentChanged(ReceiptDocument? value)
    {
        PrintCommand.RaiseCanExecuteChanged();
        SavePdfCommand.RaiseCanExecuteChanged();
    }

    protected override void OnBusyChanged(bool isBusy)
    {
        base.OnBusyChanged(isBusy);
        PrintCommand.RaiseCanExecuteChanged();
        SavePdfCommand.RaiseCanExecuteChanged();
    }

    protected override async Task FetchAsync(CancellationToken ct) => _fetched = (await _api.GetReceiptAsync(ReceiptId, ct).ConfigureAwait(false)).Unwrap();

    protected override void Apply() => Document = _fetched is { } receipt ? ReceiptDocument.From(receipt, _isDuplicate, Clock) : null;

    private async Task SendAsync(bool save)
    {
        if (Document is not { } document)
        {
            return;
        }

        FormError = null;
        Notice = null;
        PrintOutcome outcome = PrintOutcome.Cancelled;
        try
        {
            await RunBusyAsync(async () =>
                outcome = save ? await _printer.SaveAsPdfAsync(document).ConfigureAwait(true) : await _printer.PrintAsync(document).ConfigureAwait(true)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            FormError = "The receipt could not be printed. Check the printer and try again.";
            return;
        }

        switch (outcome)
        {
            case PrintOutcome.Done:
                Notice = save ? "Sent to the PDF printer. Choose where to save the file." : "Sent to the printer.";
                break;
            case PrintOutcome.Unavailable:
                FormError = save
                    ? "Save as PDF isn't available: the 'Microsoft Print to PDF' printer isn't installed. Use Print and choose another PDF printer."
                    : "No printer is available. Connect or install a printer and try again.";
                break;
        }
    }
}
