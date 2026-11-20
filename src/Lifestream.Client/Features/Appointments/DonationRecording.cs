using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Receipts;
using Lifestream.Contracts.Appointments;

namespace Lifestream.Client.Features.Appointments;

/// <summary>
/// Recording a donation, wherever it is started (the Appointments screen, or Ctrl+D, the dashboard and the palette through the shell): the dialog
/// opens over the screen as it is, and a recorded donation says so with a toast that offers the receipt. The caller refreshes what it shows.
/// </summary>
public static class DonationRecording
{
    /// <summary>Opens the Record donation dialog for the appointment. True when the donation was recorded, false when the dialog was cancelled.</summary>
    public static async Task<bool> RecordAsync(ILifestreamApiClient api, IFormDialogService forms, IReceiptViewer receipts, IToastService toasts, AppointmentDetail appointment, TimeProvider clock, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(forms);
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(toasts);
        ArgumentNullException.ThrowIfNull(appointment);
        var dialog = new RecordDonationDialogViewModel(api, appointment, clock, zone);
        bool done;
        try
        {
            done = await forms.ShowAsync(dialog).ConfigureAwait(true);
        }
        finally
        {
            dialog.Dispose();
        }

        if (!done)
        {
            return false;
        }

        var receiptId = dialog.Receipt?.Id;
        toasts.Show(
            ToastKind.Success,
            receiptId is null ? $"Donation recorded for '{appointment.DonorName}'." : $"Donation recorded for '{appointment.DonorName}'. Receipt {RecordCodes.Receipt(receiptId.Value)} issued.",
            receiptId is null ? null : "View receipt",
            receiptId is null ? null : () => receipts.ShowAsync(receiptId.Value, isDuplicate: false));
        return true;
    }
}
