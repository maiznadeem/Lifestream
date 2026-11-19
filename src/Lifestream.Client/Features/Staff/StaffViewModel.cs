using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Session;
using Lifestream.Contracts;
using Lifestream.Contracts.Staff;
using ContractSortKeys = Lifestream.Contracts.SortKeys;

namespace Lifestream.Client.Features.Staff;

/// <summary>
/// The Staff screen (FEAT-20, FEAT-22, FEAT-23). The list shows no password and no photo bytes: each row asks for its thumbnail when it is first shown,
/// and the shared default avatar stands in until it arrives (or when there is none). The salary range takes digits only and says so when From is higher
/// than To. An admin can edit, reset a password (a dialog: nothing is ever shown) and delete; Delete is disabled on the signed-in admin's own record, with the
/// reason, and every other delete asks first. The demo account sees the list but can't change anything.
/// </summary>
public sealed partial class StaffViewModel : SortedRecordListViewModel<StaffRow, StaffDetail>
{
    /// <summary>How many thumbnails are fetched at the same time.</summary>
    public const int ConcurrentPhotoRequests = 4;

    private readonly ILifestreamApiClient _api;
    private readonly IFormDialogService _forms;
    private readonly ISessionService _session;
    private readonly SemaphoreSlim _photoGate = new(ConcurrentPhotoRequests);
    private readonly CancellationTokenSource _photoCts = new();

    public StaffViewModel(ILifestreamApiClient api, IListViewStateStore states, IToastService toasts, IDialogService dialogs, IFormDialogService forms, ISessionService session, IUiDispatcher ui)
        : base(states, "staff", toasts, dialogs, ui)
    {
        _api = api;
        _forms = forms;
        _session = session;
        ResetPasswordCommand = new AsyncRelayCommand(_ => ResetPasswordAsync(), () => Detail is not null && !IsEditing && CanManageStaff);
        LoadPhotoCommand = new AsyncRelayCommand((parameter, _) => parameter is StaffRow row ? LoadPhotoAsync(row) : Task.CompletedTask);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Detail) or nameof(Editor))
            {
                ResetPasswordCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(DeleteDisabledReason));
                OnPropertyChanged(nameof(HasDeleteDisabledReason));
            }
        };
    }

    public override string PageTitle => "Staff";

    protected override string PluralName => "staff members";

    protected override string RecordType => "staff member";

    public AsyncRelayCommand ResetPasswordCommand { get; }

    public AsyncRelayCommand LoadPhotoCommand { get; }

    /// <summary>False for the demo account: it can view staff but not change anything.</summary>
    public bool CanManageStaff => _session.User?.CanManageStaff == true;

    public string? ReadOnlyNotice => CanManageStaff ? null : "The demo account can view staff but can't add, change or delete anyone.";

    public override AsyncRelayCommand? NewCommand => CanManageStaff ? AddCommand : null;

    protected override bool SupportsUndo => false;

    /// <summary>Why Delete is disabled for the selected record (shown as its tooltip), or null when it is enabled.</summary>
    public string? DeleteDisabledReason => Detail is { } staff ? WhyNotDeletable(staff) : null;

    public bool HasDeleteDisabledReason => DeleteDisabledReason is not null;

    protected override bool CanDelete(StaffDetail detail) => WhyNotDeletable(detail) is null;

    private string? WhyNotDeletable(StaffDetail staff)
    {
        if (!CanManageStaff)
        {
            return "The demo account can't delete staff.";
        }

        if (_session.User is { } me && me.StaffId == staff.Id)
        {
            return "You can't delete your own account.";
        }

        return staff.IsProtected ? "This account is protected and can't be deleted." : null;
    }

    // ---- the salary range --------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string SalaryFromText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SalaryToText { get; set; } = string.Empty;

    /// <summary>"From can't be higher than To." while the range is the wrong way round; the list is not filtered until it is fixed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSalaryRangeError))]
    public partial string? SalaryRangeError { get; set; }

    public bool HasSalaryRangeError => !string.IsNullOrEmpty(SalaryRangeError);

    private int? SalaryFrom => ParseDigits(SalaryFromText);

    private int? SalaryTo => ParseDigits(SalaryToText);

    private bool HasSalaryFilter => SalaryRangeError is null && (SalaryFrom is not null || SalaryTo is not null);

    protected override bool HasExtraFilters => HasSalaryFilter || SalaryRangeError is not null;

    partial void OnSalaryFromTextChanged(string value)
    {
        if (DigitsOnly(value) is var clean && clean != value)
        {
            SalaryFromText = clean; // letters, signs and spaces are dropped as they arrive
            return;
        }

        SalaryRangeChanged();
    }

    partial void OnSalaryToTextChanged(string value)
    {
        if (DigitsOnly(value) is var clean && clean != value)
        {
            SalaryToText = clean;
            return;
        }

        SalaryRangeChanged();
    }

    private void SalaryRangeChanged()
    {
        if (SalaryFrom is { } from && SalaryTo is { } to && from > to)
        {
            SalaryRangeError = "From can't be higher than To.";
            OnFiltersChanged();
            return;
        }

        SalaryRangeError = null;
        FilterChanged();
    }

    protected override void ResetExtraFilters()
    {
        SalaryFromText = string.Empty;
        SalaryToText = string.Empty;
        SalaryRangeError = null;
    }

    private static string DigitsOnly(string text) => new([.. text.Where(char.IsAsciiDigit)]);

    private static int? ParseDigits(string text) => int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    // ---- the list ----------------------------------------------------------------------------------------------------

    /// <summary>The columns that sort, as the API's validator names them (the name column sorts; a username sorts through the same key list).</summary>
    public override IReadOnlyList<string> SortKeys { get; } = ContractSortKeys.Staff;

    /// <summary>The sort, the search and the salary range all go to the API (server-side), so a page and its total always agree with what is filtered.</summary>
    protected override async Task<ApiResult<PagedResult<StaffRow>>> FetchSortedAsync(ListRequest request, CancellationToken ct)
    {
        var range = HasSalaryFilter;
        var result = await _api.GetStaffAsync(request.Search, request.Page, request.PageSize, request.Sort, range ? SalaryFrom : null, range ? SalaryTo : null, ct).ConfigureAwait(false);
        return result.IsOk
            ? ApiResult<PagedResult<StaffRow>>.Ok(new PagedResult<StaffRow>([.. result.Value.Items.Select(i => new StaffRow(i))], result.Value.Total, result.Value.Page, result.Value.PageSize))
            : ApiResult<PagedResult<StaffRow>>.Fail(result.Problem!);
    }

    /// <summary>Fetches the thumbnail of a row when it is first shown. A row without a photo, or already asked for, makes no request; a failure leaves the default avatar.</summary>
    public async Task LoadPhotoAsync(StaffRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.ClaimPhotoRequest())
        {
            return;
        }

        try
        {
            await _photoGate.WaitAsync(_photoCts.Token).ConfigureAwait(true);
            try
            {
                var result = await Task.Run(() => _api.GetStaffPhotoAsync(row.Id, _photoCts.Token)).ConfigureAwait(true);
                if (result.IsOk)
                {
                    row.Photo = result.Value;
                }
            }
            finally
            {
                _photoGate.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The screen was left: nobody is waiting for the photo.
        }
    }

    protected override int IdOf(StaffRow item) => item.Id;

    protected override string NameOf(StaffRow item) => item.DisplayName;

    protected override int IdOfDetail(StaffDetail detail) => detail.Id;

    protected override string NameOfDetail(StaffDetail detail) => detail.DisplayName;

    protected override Task<ApiResult<StaffDetail>> GetDetailAsync(int id, CancellationToken ct) => _api.GetStaffMemberAsync(id, ct);

    protected override IReadOnlyList<DetailFact> FactsOf(StaffDetail s) =>
    [
        new("Username", s.Username),
        new("Role", s.Role.ToString()),
        new("Designation", s.Designation.ToString()),
        new("Shift", StaffText.Shift(s.Shift)),
        new("Gender", s.Gender.ToString()),
        new("Date of birth", Formatting.Date(s.DateOfBirth)),
        new("Phone", string.IsNullOrEmpty(s.Phone) ? "Not given" : s.Phone),
        new("Salary", s.Salary is null ? "Not given" : StaffText.Salary(s.Salary)),
    ];

    protected override Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct) => _api.DeleteStaffAsync(id, ct);

    protected override Task<ApiResult<StaffDetail>> RestoreAsync(StaffDetail deleted, CancellationToken ct) =>
        Task.FromResult(ApiResult<StaffDetail>.Fail(new ApiProblem(409, ProblemCodes.Conflict, "A deleted staff member can't be restored. Add them again.")));

    protected override RecordEditorViewModel CreateEditor(StaffDetail? existing) =>
        existing is null ? StaffEditorViewModel.ForNew(_api, Toasts, Dialogs, Clock) : StaffEditorViewModel.ForEdit(_api, Toasts, Dialogs, existing, Clock);

    private async Task ResetPasswordAsync()
    {
        if (Detail is not { } staff)
        {
            return;
        }

        var dialog = new ResetPasswordDialogViewModel(_api, staff.Id, staff.DisplayName);
        bool done;
        try
        {
            done = await _forms.ShowAsync(dialog).ConfigureAwait(true);
        }
        finally
        {
            dialog.Dispose();
        }

        if (done)
        {
            Toasts.Show(ToastKind.Success, dialog.SuccessMessage);
        }
    }

    private bool _disposed;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true; // the navigation service and the service container may both dispose the screen
            _photoCts.Cancel();
            _photoCts.Dispose();
            _photoGate.Dispose();
        }

        base.Dispose(disposing);
    }
}
