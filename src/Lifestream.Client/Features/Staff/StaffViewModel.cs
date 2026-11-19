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
    private readonly HashSet<StaffRow> _deferredPhotos = [];
    private readonly HashSet<StaffRow> _photosInFlight = [];
    private IServerStatus? _resumeWatch;
    private CancellationTokenSource _photoCts = new();

    public StaffViewModel(ILifestreamApiClient api, IListViewStateStore states, IToastService toasts, IDialogService dialogs, IFormDialogService forms, ISessionService session, IUiDispatcher ui)
        : base(states, "staff", toasts, dialogs, ui)
    {
        _api = api;
        _forms = forms;
        _session = session;
        ResetPasswordCommand = Command(_ => ResetPasswordAsync(), () => Detail is not null && !IsEditing && CanManageStaff, opensModal: true);
        LoadPhotoCommand = Command((parameter, _) => parameter is StaffRow row ? LoadPhotoAsync(row) : Task.CompletedTask);
        ClosePanelCommand = Command(async _ =>
        {
            if (Editor is { } editor)
            {
                await editor.CancelCommand.ExecuteAsync(null).ConfigureAwait(true); // asks "Discard changes?" when there are any
                if (Editor is not null)
                {
                    return; // the user kept editing
                }
            }

            SelectedItem = null;
            await LoadDetailAsync(null).ConfigureAwait(true);
        });
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Detail) or nameof(Editor))
            {
                ResetPasswordCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(DeleteDisabledReason));
                OnPropertyChanged(nameof(HasDeleteDisabledReason));
                OnPropertyChanged(nameof(DetailInitials));
                OnPropertyChanged(nameof(DetailIsAdmin));
                OnPropertyChanged(nameof(DetailIsProtected));
                OnPropertyChanged(nameof(DetailStaffAccessText));
                OnPropertyChanged(nameof(DetailSubtitle));
                OnPropertyChanged(nameof(PanelOverline));
            }

            if (e.PropertyName == nameof(State))
            {
                OnStateChangedForPhotos();
            }

            if (e.PropertyName is nameof(SelectedItem) or nameof(Detail) or nameof(DetailState) or nameof(Editor))
            {
                OnPropertyChanged(nameof(IsPanelOpen));
            }
        };
    }

    public override string PageTitle => "Staff";

    protected override string PluralName => "staff members";

    protected override string RecordType => "staff member";

    protected override bool RecordIsPerson => true;

    public AsyncRelayCommand ResetPasswordCommand { get; }

    public AsyncRelayCommand LoadPhotoCommand { get; }

    /// <summary>Closes the side panel: cancels the form when one is open (asking first when it has changes), then clears the selection.</summary>
    public AsyncRelayCommand ClosePanelCommand { get; }

    /// <summary>True when the side panel is open: a person is selected or opened by id, or the add or edit form is open.</summary>
    public bool IsPanelOpen => SelectedItem is not null || Detail is not null || DetailState != LoadState.Ready || Editor is not null;

    /// <summary>The panel's header line: "Staff member S-0012", or what the form is doing.</summary>
    public string PanelOverline => Editor is not null
        ? (Detail is { } editing && IsEditing ? $"Editing {Receipts.RecordCodes.Staff(editing.Id)}" : "New staff member")
        : Detail is { } staff ? $"Staff member {Receipts.RecordCodes.Staff(staff.Id)}" : "Staff member";

    /// <summary>The selected person's initials, for the panel's avatar.</summary>
    public string DetailInitials => StaffText.Initials(Detail?.DisplayName);

    /// <summary>True when the selected person is an administrator.</summary>
    public bool DetailIsAdmin => Detail?.Role == Lifestream.Domain.Role.Admin;

    /// <summary>True when the selected person's account is protected (the owner account: it can't be deleted or demoted).</summary>
    public bool DetailIsProtected => Detail?.IsProtected == true;

    /// <summary>What the selected person can do with staff accounts: the last line of the panel's Access list.</summary>
    public string DetailStaffAccessText => DetailIsAdmin ? "Staff accounts: add, change and reset passwords" : "Staff list, view only";

    /// <summary>"Doctor, morning shift": what the person does, under their name.</summary>
    public string DetailSubtitle => Detail is { } s ? $"{StaffText.JobTitle(s.Designation)}, {(s.Shift == Lifestream.Domain.Shift.FullTime ? "full time" : $"{s.Shift.ToString().ToLowerInvariant()} shift")}" : string.Empty;

    /// <summary>The one line the read-only account sees above the list, instead of disabled buttons everywhere.</summary>
    public bool IsReadOnly => !CanManageStaff;

    /// <summary>False for the demo account: it can view staff but not change anything.</summary>
    public bool CanManageStaff => _session.User?.CanManageStaff == true;

    public string? ReadOnlyNotice => CanManageStaff ? null : "The demo account can view staff but can't add, change or delete anyone.";

    public override AsyncRelayCommand? NewCommand => CanManageStaff ? AddCommand : null;

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

    /// <summary>The salary chip's words: "Salary" with no range, else the range ("Salary: 2,000–3,000").</summary>
    public string SalaryChipText => StaffText.SalaryChip(SalaryFrom, SalaryTo);

    /// <summary>True while a salary range is set (the chip shows it as set).</summary>
    public bool HasSalaryRange => SalaryFrom is not null || SalaryTo is not null;

    /// <summary>Empties the salary range (the popup's Clear).</summary>
    public AsyncRelayCommand ClearSalaryCommand => field ??= Command(_ =>
    {
        SalaryFromText = string.Empty;
        SalaryToText = string.Empty;
        return Task.CompletedTask;
    });

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
        OnPropertyChanged(nameof(SalaryChipText));
        OnPropertyChanged(nameof(HasSalaryRange));
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
        if (_disposed)
        {
            return;
        }

        if (State is LoadState.Loading or LoadState.Waking || _resumeWatch is not null)
        {
            // Rows shown while the list is (re)loading, or while the server is waking, wait for it: a photo request then would sit behind the load's waking banner, out of reach of
            // its Cancel. They are fetched once the list is ready (OnStateChangedForPhotos).
            _deferredPhotos.Add(row);
            return;
        }

        if (!row.ClaimPhotoRequest())
        {
            return;
        }

        var token = _photoCts.Token;
        _photosInFlight.Add(row);
        try
        {
            await _photoGate.WaitAsync(token).ConfigureAwait(true);
            try
            {
                var result = await RunOperationAsync(ct => _api.GetStaffPhotoAsync(row.Id, ct), token).ConfigureAwait(true);
                if (result.IsOk)
                {
                    row.Photo = result.Value;
                }
                else if (token.IsCancellationRequested)
                {
                    row.ReleasePhotoRequest();
                }
            }
            finally
            {
                _photoGate.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // A load started (the rows are being replaced), the server started waking, or the screen was left. The row may ask again.
            row.ReleasePhotoRequest();
        }
        finally
        {
            _photosInFlight.Remove(row);
        }
    }

    /// <summary>
    /// A call of this screen waits on a waking server. Thumbnails are not worth waiting for: the photo requests in flight are cancelled at once (so no
    /// request is left that the screen's Cancel can't reach), and the rows ask again when the server is awake.
    /// </summary>
    protected override void OnOperationWakingChanged(bool isWaking)
    {
        base.OnOperationWakingChanged(isWaking);
        if (!isWaking || _disposed || _photosInFlight.Count == 0)
        {
            return;
        }

        _deferredPhotos.UnionWith(_photosInFlight);
        _photoCts.Cancel();
        _photoCts.Dispose();
        _photoCts = new CancellationTokenSource();
        if (_resumeWatch is null && CurrentServerStatus is { } status)
        {
            _resumeWatch = status;
            status.Changed += OnServerAwake;
        }
    }

    private void OnServerAwake(object? sender, EventArgs e)
    {
        if (_resumeWatch is not { State: not ServerState.Waking } status)
        {
            return;
        }

        status.Changed -= OnServerAwake;
        _resumeWatch = null;
        Post(() => OnStateChangedForPhotos());
    }

    /// <summary>
    /// A load starting cancels the photo requests in flight (they belong to the rows being replaced, and the load's Cancel must reach every request);
    /// once the list is ready, the rows that were shown during the load fetch their photos.
    /// </summary>
    private void OnStateChangedForPhotos()
    {
        if (_disposed)
        {
            return;
        }

        if (State is LoadState.Loading or LoadState.Waking)
        {
            if (!_photoCts.IsCancellationRequested)
            {
                _photoCts.Cancel();
            }

            return;
        }

        if (_photoCts.IsCancellationRequested)
        {
            _photoCts.Dispose();
            _photoCts = new CancellationTokenSource();
        }

        if (State == LoadState.Ready && _deferredPhotos.Count > 0)
        {
            var shown = _deferredPhotos.Where(Items.Contains).ToList();
            _deferredPhotos.Clear();
            foreach (var row in shown)
            {
                _ = LoadPhotoAsync(row);
            }
        }
    }

    protected override int IdOf(StaffRow item) => item.Id;

    protected override string NameOf(StaffRow item) => item.DisplayName;

    protected override int IdOfDetail(StaffDetail detail) => detail.Id;

    protected override string NameOfDetail(StaffDetail detail) => detail.DisplayName;

    protected override Task<ApiResult<StaffDetail>> GetDetailAsync(int id, CancellationToken ct) => _api.GetStaffMemberAsync(id, ct);

    /// <summary>
    /// The detail pane's lines. Phone and salary come from the detail endpoint (the list never carries them) and are shown to an administrator only.
    /// </summary>
    protected override IReadOnlyList<DetailFact> FactsOf(StaffDetail s)
    {
        List<DetailFact> facts =
        [
            new("Username", s.Username),
            new("Job title", StaffText.JobTitle(s.Designation)),
            new("Shift", StaffText.Shift(s.Shift)),
            new("Gender", s.Gender.ToString()),
            new("Date of birth", Formatting.Date(s.DateOfBirth)),
        ];
        if (_session.IsInRole(Lifestream.Domain.Role.Admin))
        {
            facts.Add(new("Phone", string.IsNullOrEmpty(s.Phone) ? "Not given" : s.Phone));
            facts.Add(new("Salary", s.Salary is null ? "Not given" : StaffText.Salary(s.Salary)));
        }

        return facts;
    }

    protected override Task<ApiResult> DeleteRecordAsync(int id, CancellationToken ct) => _api.DeleteStaffAsync(id, ct);

    // The API keeps the deleted person for a few seconds and puts them back as they were (same id, password hash, lock state, photo), so Undo restores rather than re-creates.
    protected override Task<ApiResult<StaffDetail>> RestoreAsync(StaffDetail deleted, CancellationToken ct) => _api.RestoreStaffAsync(deleted.Id, ct);

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
            if (_resumeWatch is { } status)
            {
                status.Changed -= OnServerAwake;
                _resumeWatch = null;
            }
        }

        base.Dispose(disposing);
    }
}
