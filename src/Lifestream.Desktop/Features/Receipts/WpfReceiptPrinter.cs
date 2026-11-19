using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Lifestream.Client.Features.Receipts;

namespace Lifestream.Desktop.Features.Receipts;

/// <summary>
/// Prints a receipt with the standard print dialog, or to the "Microsoft Print to PDF" printer, which asks where to save the file. Tests never use this class: they
/// inject a fake <see cref="IReceiptPrinter"/>, so no print dialog can open in a test run.
/// </summary>
public sealed class WpfReceiptPrinter : IReceiptPrinter
{
    /// <summary>The name of the PDF printer that ships with Windows.</summary>
    public const string PdfPrinterName = "Microsoft Print to PDF";

    public Task<PrintOutcome> PrintAsync(ReceiptDocument document, CancellationToken ct = default) =>
        OnUi(() =>
        {
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true)
            {
                return PrintOutcome.Cancelled;
            }

            Send(dialog, document);
            return PrintOutcome.Done;
        });

    public Task<PrintOutcome> SaveAsPdfAsync(ReceiptDocument document, CancellationToken ct = default) =>
        OnUi(() =>
        {
            var queue = FindPdfPrinter();
            if (queue is null)
            {
                return PrintOutcome.Unavailable;
            }

            Send(new PrintDialog { PrintQueue = queue }, document);
            return PrintOutcome.Done;
        });

    private static PrintQueue? FindPdfPrinter()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues().FirstOrDefault(q => string.Equals(q.Name, PdfPrinterName, StringComparison.OrdinalIgnoreCase));
        }
        catch (PrintQueueException)
        {
            return null; // no print spooler: nothing to print to
        }
    }

    private static void Send(PrintDialog dialog, ReceiptDocument receipt)
    {
        var flow = ReceiptDocumentBuilder.Build(receipt);
        flow.PageWidth = dialog.PrintableAreaWidth;
        flow.PageHeight = dialog.PrintableAreaHeight;
        dialog.PrintDocument(((IDocumentPaginatorSource)flow).DocumentPaginator, $"Receipt {receipt.Number}");
    }

    private static Task<PrintOutcome> OnUi(Func<PrintOutcome> action)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.CheckAccess() ? Task.FromResult(action()) : dispatcher.InvokeAsync(action).Task;
    }
}
