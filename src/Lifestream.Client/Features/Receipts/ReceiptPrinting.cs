using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts.Receipts;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Receipts;

/// <summary>What happened to a print or save request.</summary>
public enum PrintOutcome
{
    /// <summary>Sent to the printer (or the PDF printer).</summary>
    Done,

    /// <summary>The user closed the print dialog.</summary>
    Cancelled,

    /// <summary>There is no printer to use (for Save as PDF: the "Microsoft Print to PDF" printer is not installed).</summary>
    Unavailable,
}

/// <summary>Prints a receipt. The Desktop project builds a FlowDocument and shows the print dialog; tests inject a fake, so no print dialog ever opens in a test.</summary>
public interface IReceiptPrinter
{
    Task<PrintOutcome> PrintAsync(ReceiptDocument document, CancellationToken ct = default);

    /// <summary>Prints to the "Microsoft Print to PDF" printer, which asks where to save the file.</summary>
    Task<PrintOutcome> SaveAsPdfAsync(ReceiptDocument document, CancellationToken ct = default);
}

/// <summary>Used when the app registered no printer (headless hosts): every request reports that there is nothing to print to.</summary>
public sealed class NoReceiptPrinter : IReceiptPrinter
{
    public Task<PrintOutcome> PrintAsync(ReceiptDocument document, CancellationToken ct = default) => Task.FromResult(PrintOutcome.Unavailable);

    public Task<PrintOutcome> SaveAsPdfAsync(ReceiptDocument document, CancellationToken ct = default) => Task.FromResult(PrintOutcome.Unavailable);
}

/// <summary>
/// What a printed receipt says, as plain label and value lines, so the screen, the print preview and the printer all show the same text (FEAT-18).
/// A receipt can be printed again at any time: a copy printed after the first says so in <see cref="CopyNotice"/>.
/// </summary>
public sealed record ReceiptDocument(string Title, string Number, IReadOnlyList<DetailFact> Lines, string? Notes, string? CopyNotice, string IssuedBy)
{
    public static ReceiptDocument From(ReceiptDetail receipt, bool isDuplicate, TimeProvider? clock = null, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var donation = receipt.Kind == ReceiptKind.Donation;
        var lines = new List<DetailFact>
        {
            new("Receipt", RecordCodes.Receipt(receipt.Id)),
            new("Date", RecordFormatting.When(receipt.CreatedAtUtc, zone)),
            new(donation ? "Donor" : "Patient", receipt.PartyName),
            new("Blood group", receipt.BloodType.ToDisplay()),
            new("Units", receipt.Units.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (receipt.AppointmentId is { } appointment)
        {
            lines.Add(new DetailFact("Appointment", $"{receipt.PartyName} ({RecordCodes.Appointment(appointment)})"));
        }

        if (receipt.RequestId is { } request)
        {
            lines.Add(new DetailFact("Request", $"{receipt.PartyName} ({RecordCodes.Request(request)})"));
        }

        lines.Add(new DetailFact("Issued by", $"{receipt.StaffName} ({RecordCodes.Staff(receipt.StaffId)})"));
        if (receipt.BagIds.Count > 0)
        {
            lines.Add(new DetailFact(receipt.BagIds.Count == 1 ? "Blood bag" : "Blood bags", string.Join(", ", receipt.BagIds.Select(RecordCodes.Bag))));
        }

        var printedAt = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        return new ReceiptDocument(
            donation ? "Donation receipt" : "Blood issue receipt",
            RecordCodes.Receipt(receipt.Id),
            lines,
            string.IsNullOrWhiteSpace(receipt.Notes) ? null : receipt.Notes,
            isDuplicate ? $"Duplicate copy printed {RecordFormatting.When(printedAt, zone)}" : null,
            receipt.StaffName);
    }
}
