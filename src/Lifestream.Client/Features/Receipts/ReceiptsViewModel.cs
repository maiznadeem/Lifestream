using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Navigation;
using Lifestream.Contracts;
using Lifestream.Contracts.Receipts;
using Lifestream.Domain;
using ContractSortKeys = Lifestream.Contracts.SortKeys;

namespace Lifestream.Client.Features.Receipts;

/// <summary>One row of the receipts list: names first with the ids as secondary text, never a bare id (FEAT-14, FEAT-17).</summary>
public sealed class ReceiptRow
{
    public ReceiptRow(ReceiptListItem item, TimeZoneInfo zone)
    {
        Item = item;
        Zone = zone;
        Id = item.Id;
    }

    public ReceiptListItem Item { get; }

    public TimeZoneInfo Zone { get; }

    public int Id { get; }

    public string WhenText => RecordFormatting.When(Item.CreatedAtUtc, Zone);

    public string KindText => RecordFormatting.Kind(Item.Kind);

    public string BloodTypeText => Item.BloodType.ToDisplay();

    public string UnitsText => Item.Units.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The donor of a donation or the patient of an issue.</summary>
    public string PartyName => Item.PartyName;

    /// <summary>"Appointment" or "Request": the record the receipt belongs to.</summary>
    public string SourceKindText => Item.Kind == ReceiptKind.Donation ? "Appointment" : "Request";

    /// <summary>"#12": the id of that appointment or request, as secondary text under the party's name.</summary>
    public string SourceIdText => RecordFormatting.IdText((Item.Kind == ReceiptKind.Donation ? Item.AppointmentId : Item.RequestId) ?? 0);

    public string StaffName => Item.StaffName;

    /// <summary>The issuing staff member's initials, for the compact "Issued by" avatar (the full name is its tooltip).</summary>
    public string StaffInitials => Staff.StaffText.Initials(Item.StaffName);

    /// <summary>True for a blood issue receipt, false for a donation receipt (the kind pill's icon and colour).</summary>
    public bool IsIssue => Item.Kind == ReceiptKind.Issue;

    /// <summary>The group as set in type (A−).</summary>
    public string GroupText => Item.BloodType.ToTypographic();

    public string StaffIdText => RecordFormatting.IdText(Item.StaffId);
}

/// <summary>
/// The Receipts screen (FEAT-17, FEAT-18). A paged list with text search (receipt number, donor or patient name), a kind filter and a blood type
/// filter; every row names the staff member and the party, and any receipt opens in its printable view, which can be printed again whenever needed.
/// Receipts are issued by recording a donation or fulfilling a request, so there is no "add" here.
/// </summary>
public sealed class ReceiptsViewModel : SortedRecordListViewModel<ReceiptRow, ReceiptDetail>
{
    private const string KindKey = "kind";

    private readonly ILifestreamApiClient _api;
    private readonly IReceiptViewer _viewer;
    private readonly INavigationService _navigation;
    private readonly IReceiptPrinter? _printer;
    private FilterOption<ReceiptKind> _kindFilter;

    public ReceiptsViewModel(
        ILifestreamApiClient api,
        IListViewStateStore states,
        IToastService toasts,
        IDialogService dialogs,
        IReceiptViewer viewer,
        INavigationService navigation,
        IUiDispatcher ui)
        : this(api, states, toasts, dialogs, viewer, navigation, ui, null)
    {
    }

    /// <summary>The screen as the app makes it: <paramref name="printer"/> lets the side panel print the selected receipt, or save it as a PDF, without opening it first.</summary>
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public ReceiptsViewModel(
        ILifestreamApiClient api,
        IListViewStateStore states,
        IToastService toasts,
        IDialogService dialogs,
        IReceiptViewer viewer,
        INavigationService navigation,
        IUiDispatcher ui,
        IReceiptPrinter? printer)
        : base(states, "receipts", toasts, dialogs, ui)
    {
        _api = api;
        _printer = printer;
        PrintCommand = Command(_ => SendAsync(save: false), () => Detail is not null && _printer is not null && !IsBusy);
        SavePdfCommand = Command(_ => SendAsync(save: true), () => Detail is not null && _printer is not null && !IsBusy);
        ClosePanelCommand = Command(_ =>
        {
            SelectedItem = null;
            return LoadDetailAsync(null); // also closes a receipt opened by id from another screen
        });
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Detail))
            {
                Preview = Detail is { } receipt ? ReceiptDocument.From(receipt, isDuplicate: false, Clock, Zone) : null;
                PrintCommand.RaiseCanExecuteChanged();
                SavePdfCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(PanelOverline));
            }
            if (e.PropertyName is nameof(SelectedItem) or nameof(Detail) or nameof(DetailState))
            {
                OnPropertyChanged(nameof(IsPanelOpen));
            }
        };
        _viewer = viewer;
        _navigation = navigation;
        KindOptions = SharedLists.WithAll(SharedLists.ReceiptKinds, "All receipts");
        _kindFilter = KindOptions[0];
        if (ViewState.Filters.TryGetValue(KindKey, out var saved) && Enum.TryParse<ReceiptKind>(saved, out var kind))
        {
            _kindFilter = KindOptions.First(o => o.Value == kind);
        }

        OpenReceiptCommand = Command((parameter, _) =>
        {
            var id = parameter switch
            {
                ReceiptRow row => row.Id,
                _ => Detail?.Id,
            };
            return id is { } receiptId ? _viewer.ShowAsync(receiptId, isDuplicate: true) : Task.CompletedTask;
        }, opensModal: true);
        OpenPartyCommand = Command((parameter, _) => parameter is ReceiptRow row ? OpenPartyAsync(row) : Task.CompletedTask);
        OpenSourceCommand = Command(async (parameter, _) =>
        {
            // The appointment or request the receipt was issued for, opened by its id.
            if (parameter is ReceiptRow { Item: { } item })
            {
                if (item.Kind == ReceiptKind.Donation && item.AppointmentId is { } appointment)
                {
                    await _navigation.NavigateTo<AppointmentsViewModel>(new ListPrefilter(RecordId: appointment)).ConfigureAwait(true);
                }
                else if (item.Kind == ReceiptKind.Issue && item.RequestId is { } request)
                {
                    await _navigation.NavigateTo<RequestsViewModel>(new ListPrefilter(RecordId: request)).ConfigureAwait(true);
                }
            }
        });
    }

    /// <summary>
    /// Opens the donor (donation) or patient (issue) the receipt names, by id. The list carries the appointment or request, not the person's id, so the person's
    /// id comes from that record; if it can't be fetched the user is told and nothing is searched by name instead.
    /// </summary>
    private async Task OpenPartyAsync(ReceiptRow row)
    {
        var item = row.Item;
        int? personId = null;
        if (item.Kind == ReceiptKind.Donation && item.AppointmentId is { } appointmentId)
        {
            var appointment = await RunOperationAsync(ct => _api.GetAppointmentAsync(appointmentId, ct)).ConfigureAwait(true);
            personId = appointment.IsOk ? appointment.Value.DonorId : null;
        }
        else if (item.Kind == ReceiptKind.Issue && item.RequestId is { } requestId)
        {
            var request = await RunOperationAsync(ct => _api.GetRequestAsync(requestId, ct)).ConfigureAwait(true);
            personId = request.IsOk ? request.Value.PatientId : null;
        }

        if (personId is not { } id)
        {
            Toasts.Show(ToastKind.Error, $"Couldn't open {row.PartyName}. Try again.");
            return;
        }

        var prefilter = new ListPrefilter(RecordId: id);
        await (item.Kind == ReceiptKind.Donation ? _navigation.NavigateTo<DonorsViewModel>(prefilter) : _navigation.NavigateTo<PatientsViewModel>(prefilter)).ConfigureAwait(true);
    }

    public override string PageTitle => "Receipts";

    protected override string PluralName => "receipts";

    protected override string RecordType => "receipt";

    /// <summary>Receipts are never added by hand: no Ctrl+N and no add button.</summary>
    public override AsyncRelayCommand? NewCommand => null;

    protected override string? NewActionText => null;

    protected override string EmptyAddMessage => "Receipts appear here when a donation is recorded or a request is fulfilled.";

    internal TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public IReadOnlyList<FilterOption<ReceiptKind>> KindOptions { get; }

    public AsyncRelayCommand OpenReceiptCommand { get; }

    public AsyncRelayCommand OpenPartyCommand { get; }

    public AsyncRelayCommand OpenSourceCommand { get; }

    /// <summary>Prints the selected receipt as a duplicate copy (it was issued before).</summary>
    public AsyncRelayCommand PrintCommand { get; }

    /// <summary>Saves the selected receipt as a PDF (a duplicate copy).</summary>
    public AsyncRelayCommand SavePdfCommand { get; }

    /// <summary>Closes the side panel (clears the selection).</summary>
    public AsyncRelayCommand ClosePanelCommand { get; }

    /// <summary>True when the side panel shows a receipt: a row is selected, or one was opened by id (loading, shown, or failed to load).</summary>
    public bool IsPanelOpen => SelectedItem is not null || Detail is not null || DetailState != LoadState.Ready;

    /// <summary>"Donation receipt #12": the panel's header line.</summary>
    public string? PanelOverline => Detail is { } receipt ? $"{(receipt.Kind == ReceiptKind.Donation ? "Donation" : "Blood issue")} receipt {RecordFormatting.IdText(receipt.Id)}" : null;

    /// <summary>The selected receipt as it prints, for the panel's preview (the same document the printer gets, without the duplicate line).</summary>
    public ReceiptDocument? Preview
    {
        get;
        private set => SetProperty(ref field, value);
    }

    private async Task SendAsync(bool save)
    {
        if (Detail is not { } receipt || _printer is null)
        {
            return;
        }

        var document = ReceiptDocument.From(receipt, isDuplicate: true, Clock, Zone);
        PrintOutcome outcome;
        try
        {
            outcome = save ? await _printer.SaveAsPdfAsync(document).ConfigureAwait(true) : await _printer.PrintAsync(document).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Toasts.Show(ToastKind.Error, "The receipt could not be printed. Check the printer and try again.");
            return;
        }

        switch (outcome)
        {
            case PrintOutcome.Done:
                Toasts.Show(ToastKind.Success, save ? "Sent to the PDF printer. Choose where to save the file." : "Receipt sent to the printer.");
                break;
            case PrintOutcome.Unavailable:
                Toasts.Show(ToastKind.Error, save
                    ? "Save as PDF isn't available: the 'Microsoft Print to PDF' printer isn't installed. Use Print and choose another PDF printer."
                    : "No printer is available. Connect or install a printer and try again.");
                break;
        }
    }

    public FilterOption<ReceiptKind> KindFilter
    {
        get => _kindFilter;
        set
        {
            if (SetProperty(ref _kindFilter, value))
            {
                ViewState.Filters[KindKey] = value.Value?.ToString();
                FilterChanged();
            }
        }
    }

    protected override bool HasExtraFilters => base.HasExtraFilters || _kindFilter.Value is not null;

    protected override void ResetExtraFilters()
    {
        base.ResetExtraFilters();
        _kindFilter = KindOptions[0];
        OnPropertyChanged(nameof(KindFilter));
    }

    /// <summary>The only column that sorts is the date (the API supports exactly "date" for receipts).</summary>
    public override IReadOnlyList<string> SortKeys { get; } = ContractSortKeys.Receipts;

    protected override async Task<ApiResult<PagedResult<ReceiptRow>>> FetchSortedAsync(ListRequest request, CancellationToken ct)
    {
        var result = await _api.GetReceiptsAsync(new ReceiptQuery(request.Search, _kindFilter.Value, BloodTypeValue, null, null, request.Page, request.PageSize, request.Sort), ct).ConfigureAwait(false);
        if (!result.IsOk)
        {
            return ApiResult<PagedResult<ReceiptRow>>.Fail(result.Problem!);
        }

        var page = result.Value;
        return ApiResult<PagedResult<ReceiptRow>>.Ok(new PagedResult<ReceiptRow>([.. page.Items.Select(i => new ReceiptRow(i, Zone))], page.Total, page.Page, page.PageSize));
    }

    protected override int IdOf(ReceiptRow item) => item.Id;

    protected override string NameOf(ReceiptRow item) => item.PartyName;

    protected override int IdOfDetail(ReceiptDetail detail) => detail.Id;

    protected override string NameOfDetail(ReceiptDetail detail) => $"Receipt {RecordFormatting.IdText(detail.Id)}";

    protected override Task<ApiResult<ReceiptDetail>> GetDetailAsync(int id, CancellationToken ct) => _api.GetReceiptAsync(id, ct);

    protected override IReadOnlyList<DetailFact> FactsOf(ReceiptDetail receipt) => ReceiptDocument.From(receipt, isDuplicate: false, Clock, Zone).Lines;

    protected override Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct) =>
        Task.FromResult(ApiResult.Fail(new ApiProblem(405, ProblemCodes.Conflict, "A receipt can't be deleted.")));

    protected override Task<ApiResult<ReceiptDetail>> RestoreAsync(ReceiptDetail deleted, CancellationToken ct) =>
        Task.FromResult(ApiResult<ReceiptDetail>.Fail(new ApiProblem(405, ProblemCodes.Conflict, "A receipt can't be restored.")));

    protected override RecordEditorViewModel CreateEditor(ReceiptDetail? existing) =>
        throw new InvalidOperationException("Receipts are issued by recording a donation or fulfilling a request; they have no form.");
}
