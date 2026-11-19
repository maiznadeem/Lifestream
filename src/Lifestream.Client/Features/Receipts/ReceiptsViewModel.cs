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
    private FilterOption<ReceiptKind> _kindFilter;

    public ReceiptsViewModel(
        ILifestreamApiClient api,
        IListViewStateStore states,
        IToastService toasts,
        IDialogService dialogs,
        IReceiptViewer viewer,
        INavigationService navigation,
        IUiDispatcher ui)
        : base(states, "receipts", toasts, dialogs, ui)
    {
        _api = api;
        _viewer = viewer;
        _navigation = navigation;
        KindOptions = FilterOptions.WithAll(Enum.GetValues<ReceiptKind>(), RecordFormatting.Kind, "All receipts");
        _kindFilter = KindOptions[0];
        if (ViewState.Filters.TryGetValue(KindKey, out var saved) && Enum.TryParse<ReceiptKind>(saved, out var kind))
        {
            _kindFilter = KindOptions.First(o => o.Value == kind);
        }

        OpenReceiptCommand = new AsyncRelayCommand((parameter, _) =>
        {
            var id = parameter switch
            {
                ReceiptRow row => row.Id,
                _ => Detail?.Id,
            };
            return id is { } receiptId ? _viewer.ShowAsync(receiptId, isDuplicate: true) : Task.CompletedTask;
        });
        OpenPartyCommand = new AsyncRelayCommand(async (parameter, _) =>
        {
            if (parameter is ReceiptRow row)
            {
                var prefilter = new ListPrefilter(Search: row.PartyName);
                await (row.Item.Kind == ReceiptKind.Donation ? _navigation.NavigateTo<DonorsViewModel>(prefilter) : _navigation.NavigateTo<PatientsViewModel>(prefilter)).ConfigureAwait(true);
            }
        });
        OpenSourceCommand = new AsyncRelayCommand(async (parameter, _) =>
        {
            if (parameter is ReceiptRow row)
            {
                var prefilter = new ListPrefilter(Search: row.PartyName);
                await (row.Item.Kind == ReceiptKind.Donation ? _navigation.NavigateTo<AppointmentsViewModel>(prefilter) : _navigation.NavigateTo<RequestsViewModel>(prefilter)).ConfigureAwait(true);
            }
        });
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
    public override IReadOnlyList<string> SortKeys { get; } = ["date"];

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
