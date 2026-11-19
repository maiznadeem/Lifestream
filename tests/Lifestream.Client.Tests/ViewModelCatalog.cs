using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Lookups;
using Lifestream.Client.Shell;
using Lifestream.Client.Shell.Placeholders;
using Lifestream.Client.Tests.Features;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

/// <summary>
/// One factory for every view model type of the client, built on the fakes (strict about operation scopes). The inventory tests walk the client
/// assembly by reflection and fail when a view model type is missing here, so a new screen, editor or dialog cannot be added without being inventoried
/// (SHELL-07, SHELL-24). Nothing here talks to a server.
/// </summary>
public sealed class ViewModelCatalog : IDisposable
{
    private readonly Part2Rig _rig = new(Role.Admin);
    private readonly Fakes.Rig _di = new();
    private readonly List<IDisposable> _owned = [];

    public ViewModelCatalog() => _di.SignIn(Role.Admin);

    public Part2Rig Rig => _rig;

    /// <summary>The form dialogs and editors with inputs, each with the edit that makes it dirty.</summary>
    public IReadOnlyList<(Type Type, Func<IHasUnsavedChanges> Create, Action<IHasUnsavedChanges> MakeDirty)> Forms { get; private set; } = [];

    /// <summary>Every concrete view model type of the client, built on the fakes.</summary>
    public Dictionary<Type, Func<object>> Factories => field ??= Build();

    private ProfileViewModel LoadedProfile()
    {
        _rig.Fake.On("GetProfileAsync", ApiResult<Lifestream.Contracts.Profile.ProfileResponse>.Ok(Samples2.Profile()));
        var profile = _rig.Profile();
        profile.LoadAsync().GetAwaiter().GetResult(); // the form is dirty only against what was loaded
        return Own(profile);
    }

    private T Own<T>(T item)
        where T : IDisposable
    {
        _owned.Add(item);
        return item;
    }

    private Dictionary<Type, Func<object>> Build()
    {
        var api = _rig.Api;
        var toasts = _rig.Toasts;
        var dialogs = _rig.Dialogs;
        var clock = _rig.Clock;

        var forms = new List<(Type, Func<IHasUnsavedChanges>, Action<IHasUnsavedChanges>)>
        {
            (typeof(DonorEditorViewModel), () => Own(DonorEditorViewModel.ForNew(api, toasts, dialogs, clock)), f => ((DonorEditorViewModel)f).Name = "Typed"),
            (typeof(PatientEditorViewModel), () => Own(PatientEditorViewModel.ForNew(api, toasts, dialogs, clock)), f => ((PatientEditorViewModel)f).Name = "Typed"),
            (typeof(AppointmentEditorViewModel), () => Own(AppointmentEditorViewModel.ForNew(api, toasts, dialogs, clock, TimeZoneInfo.Utc)), f => ((AppointmentEditorViewModel)f).OverrideReason = "Typed"),
            (typeof(RequestEditorViewModel), () => Own(RequestEditorViewModel.ForNew(api, toasts, dialogs, clock)), f => ((RequestEditorViewModel)f).UnitsText = "2"),
            (typeof(StaffEditorViewModel), () => Own(StaffEditorViewModel.ForNew(api, toasts, dialogs, clock)), f => ((StaffEditorViewModel)f).Username = "typed"),
            (typeof(ProfileViewModel), LoadedProfile, f => ((ProfileViewModel)f).DisplayName = "Typed"),
            (typeof(RecordDonationDialogViewModel), () => Own(new RecordDonationDialogViewModel(api, Samples2.Appointment(), clock, TimeZoneInfo.Utc)), f => ((RecordDonationDialogViewModel)f).Notes = "Typed"),
            (typeof(ChangePasswordDialogViewModel), () => Own(new ChangePasswordDialogViewModel(api)), f => ((ChangePasswordDialogViewModel)f).NewPassword = "typed"),
            (typeof(ResetPasswordDialogViewModel), () => Own(new ResetPasswordDialogViewModel(api, 5, "Staff 005")), f => ((ResetPasswordDialogViewModel)f).TemporaryPassword = "typed"),
        };
        Forms = [.. forms];

        var catalog = new Dictionary<Type, Func<object>>
        {
            [typeof(LoginViewModel)] = () => Own(_di.Get<LoginViewModel>()),
            [typeof(ShellViewModel)] = () => Own(_di.Get<ShellViewModel>()),
            [typeof(AccessDeniedViewModel)] = () => Own(_di.Get<AccessDeniedViewModel>()),
            [typeof(DashboardPlaceholderViewModel)] = () => Own(new DashboardPlaceholderViewModel()),
            [typeof(DonorsPlaceholderViewModel)] = () => Own(new DonorsPlaceholderViewModel()),
            [typeof(PatientsPlaceholderViewModel)] = () => Own(new PatientsPlaceholderViewModel()),
            [typeof(StockPlaceholderViewModel)] = () => Own(new StockPlaceholderViewModel()),
            [typeof(Lifestream.Client.Features.Dashboard.DashboardViewModel)] = () => Own(_rig.Base.Dashboard()),
            [typeof(DonorsViewModel)] = () => Own(_rig.Base.Donors()),
            [typeof(PatientsViewModel)] = () => Own(_rig.Base.Patients()),
            [typeof(Lifestream.Client.Features.Stock.StockViewModel)] = () => Own(_rig.Base.Stock()),
            [typeof(AppointmentsViewModel)] = () => Own(_rig.Appointments()),
            [typeof(RequestsViewModel)] = () => Own(_rig.Requests()),
            [typeof(ReceiptsViewModel)] = () => Own(_rig.Receipts()),
            [typeof(StaffViewModel)] = () => Own(_rig.Staff()),
            [typeof(FulfilRequestDialogViewModel)] = () => Own(new FulfilRequestDialogViewModel(api, Samples2.Request())),
            [typeof(ReceiptViewerViewModel)] = () => Own(new ReceiptViewerViewModel(api, _rig.Printer, 90, false)),
            [typeof(RecordPickerViewModel)] = () => Own(new RecordPickerViewModel(api, RecordKind.Donor, clock)),
            [typeof(ToastItem)] = () => _rig.Toasts.Show(ToastKind.Info, "x", "Undo", () => Task.CompletedTask),
            [typeof(EligibilityBannerViewModel)] = () => new EligibilityBannerViewModel(),
        };
        foreach (var (type, create, _) in forms)
        {
            catalog[type] = () => create();
        }

        return catalog;
    }

    public void Dispose()
    {
        foreach (var item in _owned)
        {
            item.Dispose();
        }

        _di.Dispose();
    }
}
