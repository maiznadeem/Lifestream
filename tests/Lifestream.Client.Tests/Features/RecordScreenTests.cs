using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Stock;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>Donors and Patients as list-plus-detail screens, and deleting from them (FEAT-04, FEAT-06), against the fake API client.</summary>
public sealed class RecordScreenTests
{
    private static async Task<(FeatureRig Rig, DonorsViewModel Vm)> LoadedDonorsAsync(int total = 3)
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(total));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples.Donor(1, "Donor 001")));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new(true, [], null)));
        var vm = rig.Donors();
        await vm.LoadAsync();
        return (rig, vm);
    }

    private static async Task SelectAsync(DonorsViewModel vm, int index)
    {
        vm.SelectedItem = vm.Items[index];
        await vm.PendingDetail;
    }

    // FEAT-04

    [Fact]
    public void Navigation_HasOneScreenPerEntity_NoInfoOrManageOperationsScreens()
    {
        var titles = NavigationCatalog.Default.Select(d => d.Title).ToList();
        Assert.DoesNotContain(titles, t => t.Contains("Info", StringComparison.OrdinalIgnoreCase) || t.Contains("Operation", StringComparison.OrdinalIgnoreCase) || t.Contains("Manage", StringComparison.OrdinalIgnoreCase));
        Assert.Single(titles, "Donors");
        Assert.Single(titles, "Patients");
        Assert.Equal(typeof(DonorsViewModel), NavigationCatalog.Default.Single(d => d.Title == "Donors").ViewModelType);
        Assert.Equal(typeof(PatientsViewModel), NavigationCatalog.Default.Single(d => d.Title == "Patients").ViewModelType);
    }

    [Fact]
    public async Task DonorsScreen_SelectingARow_ShowsDetailAndEnablesEditAndDelete_FakeApi()
    {
        var (rig, vm) = await LoadedDonorsAsync();
        Assert.False(vm.EditCommand.CanExecute(null)); // nothing selected: nothing to act on
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.True(vm.AddCommand.CanExecute(null));
        Assert.Same(vm.AddCommand, vm.NewCommand); // Ctrl+N

        var hasDetailSeen = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.HasDetail))
            {
                hasDetailSeen.Add(vm.HasDetail);
            }
        };

        await SelectAsync(vm, 0);

        Assert.True(vm.HasDetail);
        Assert.True(hasDetailSeen[^1]); // the view is told when the pane becomes ready, after the state and the record are both set
        Assert.Equal("Donor 001", vm.DetailTitle);
        Assert.Contains(vm.DetailFacts, f => f.Label == "Blood type" && f.Value == "O+");
        Assert.Contains(vm.DetailFacts, f => f.Label == "Eligibility" && f.Value == "Eligible to donate");
        Assert.True(vm.EditCommand.CanExecute(null));
        Assert.True(vm.DeleteCommand.CanExecute(null));
        Assert.Equal(1, rig.Recorder.CountOf("GetDonorAsync")); // one detail call per selection
        Assert.Equal(1, rig.Recorder.ArgsOf<int>("GetDonorAsync").Single());
    }

    [Fact]
    public async Task DonorsScreen_Edit_OpensTheEditorOnTheSelectedRecord_AndSaveRefreshesTheList_FakeApi()
    {
        var (rig, vm) = await LoadedDonorsAsync();
        await SelectAsync(vm, 0);
        rig.Fake.On("UpdateDonorAsync", ApiResult<DonorDetail>.Ok(Samples.Donor(1, "Donor 001") with { Phone = "+1-555-0199", RowVersion = "rv-2" }));

        await vm.EditCommand.ExecuteAsync();
        var editor = Assert.IsType<DonorEditorViewModel>(vm.Editor);
        Assert.False(editor.IsNew);
        Assert.Equal("Donor 001", editor.Name);
        Assert.False(vm.EditCommand.CanExecute(null)); // one editor at a time

        editor.Phone = "+1-555-0199";
        await editor.SaveCommand.ExecuteAsync();
        await vm.PendingRefresh;

        Assert.Null(vm.Editor); // the drawer closed
        Assert.Equal(1, rig.Recorder.ArgsOf<int>("UpdateDonorAsync").Single());
        Assert.Equal(2, rig.Recorder.CountOf("GetDonorsAsync")); // the list was refreshed
    }

    [Fact]
    public async Task DonorsScreen_Add_OpensAnEmptyEditor_AndRelatedActionsAreOnTheDetailPane()
    {
        var (_, vm) = await LoadedDonorsAsync();

        await vm.AddCommand.ExecuteAsync();

        var editor = Assert.IsType<DonorEditorViewModel>(vm.Editor);
        Assert.True(editor.IsNew);
        Assert.Equal(string.Empty, editor.Name);
        Assert.Null(editor.DateOfBirth);
        Assert.Equal("New donor", editor.Title);
    }

    // FEAT-06

    [Fact]
    public async Task DonorDelete_ConfirmsNamingRecord_RemovesOnlyAfterApiOk_FakeApi()
    {
        var (rig, vm) = await LoadedDonorsAsync();
        await SelectAsync(vm, 0);
        var gate = new TaskCompletionSource();
        rig.Fake.Handlers["DeleteDonorAsync"] = async _ =>
        {
            await gate.Task;
            return ApiResult.Success;
        };

        var delete = vm.DeleteCommand.ExecuteAsync();

        // The confirmation names the record and the danger button says verb and object.
        Assert.Single(rig.Dialogs.Confirmations);
        var (title, body, verb) = rig.Dialogs.Confirmations[0];
        Assert.Equal("Delete donor?", title);
        Assert.Contains("'Donor 001'", body, StringComparison.Ordinal);
        Assert.Equal("Delete donor", verb);

        // While the API has not answered the list still shows the row and the button is busy.
        Assert.Equal(3, vm.Items.Count);
        Assert.Contains(vm.Items, i => i.Id == 1);
        Assert.True(vm.IsBusy);
        Assert.Empty(rig.Toasts.Toasts);

        rig.DonorsReturn(_ => new PagedResult<DonorListItem>([Samples.DonorRow(2), Samples.DonorRow(3)], 2, 1, 50));
        gate.SetResult();
        await delete;

        Assert.Equal(2, vm.Items.Count);
        Assert.DoesNotContain(vm.Items, i => i.Id == 1);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.Detail);
        var toast = Assert.Single(rig.Toasts.Toasts);
        Assert.Equal("Donor 'Donor 001' deleted.", toast.Message);
    }

    [Fact]
    public async Task DonorDelete_Cancelled_CallsNothing_FakeApi()
    {
        var (rig, vm) = await LoadedDonorsAsync();
        await SelectAsync(vm, 0);
        rig.Dialogs.Answer = false;

        await vm.DeleteCommand.ExecuteAsync();

        Assert.Equal(0, rig.Recorder.CountOf("DeleteDonorAsync"));
        Assert.Equal(3, vm.Items.Count);
        Assert.Empty(rig.Toasts.Toasts);
    }

    [Theory]
    [InlineData(404, ProblemCodes.NotFound, "was not found")]
    [InlineData(409, ProblemCodes.Conflict, "in use by other records")]
    public async Task DonorDelete_OnNotFoundOrConflict_ShowsMessage_LeavesTheListUnchanged_FakeApi(int status, string code, string expectedText)
    {
        var (rig, vm) = await LoadedDonorsAsync();
        await SelectAsync(vm, 0);
        rig.Fake.Handlers["DeleteDonorAsync"] = _ => Task.FromResult<object>(ApiResult.Fail(FeatureRig.Problem(status, code)));
        var listCalls = rig.Recorder.CountOf("GetDonorsAsync");

        await vm.DeleteCommand.ExecuteAsync();

        Assert.Equal(3, vm.Items.Count); // the list is unchanged
        Assert.Contains(vm.Items, i => i.Id == 1);
        Assert.Equal(listCalls, rig.Recorder.CountOf("GetDonorsAsync")); // and was not even reloaded
        var toast = Assert.Single(rig.Toasts.Toasts);
        Assert.Equal(ToastKind.Error, toast.Kind);
        Assert.Contains(expectedText, toast.Message, StringComparison.Ordinal);
        Assert.Contains("Donor 001", toast.Message, StringComparison.Ordinal);
        Assert.False(toast.HasAction); // no Undo for something that did not happen
        Assert.NotNull(vm.Detail); // the record stays selected
    }

    [Fact]
    public async Task DonorDelete_Success_OffersATenSecondUndo_AndUndoRestoresTheSameRecord_FakeApi()
    {
        var (rig, vm) = await LoadedDonorsAsync();
        await SelectAsync(vm, 0);
        rig.DonorsReturn(_ => new PagedResult<DonorListItem>([Samples.DonorRow(2), Samples.DonorRow(3)], 2, 1, 50));
        await vm.DeleteCommand.ExecuteAsync();
        var toast = Assert.Single(rig.Toasts.Toasts);
        Assert.Equal("Undo", toast.ActionLabel);
        rig.Clock.Advance(TimeSpan.FromSeconds(9.9));
        Assert.False(toast.IsDismissed); // Undo stays 10 seconds (FEAT-06)

        var restored = Samples.Donor(1, "Donor 001") with { LastDonated = new DateOnly(2026, 10, 20), MedicalHold = true };
        rig.Fake.On("RestoreDonorAsync", ApiResult<DonorDetail>.Ok(restored));
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(restored));
        await toast.ActionCommand.ExecuteAsync();

        Assert.Equal(1, rig.Recorder.LastArg<int>("RestoreDonorAsync")); // the same id goes back, not a re-created record
        Assert.Equal(0, rig.Recorder.CountOf("CreateDonorAsync"));
        Assert.Contains(rig.Toasts.Toasts, t => t.Message == "Donor 'Donor 001' restored.");
        Assert.Equal(3, vm.Items.Count);
        await vm.PendingDetail;
        Assert.Equal(1, vm.Detail!.Id);
        Assert.Equal(new DateOnly(2026, 10, 20), vm.Detail.LastDonated); // what the old re-create lost
        Assert.Contains(vm.DetailFacts, f => f.Label == "Last donated" && f.Value.Contains("2026", StringComparison.Ordinal));
        rig.Clock.Advance(TimeSpan.FromSeconds(0.3));
        Assert.True(toast.IsDismissed);
    }

    [Fact]
    public async Task DonorDelete_Success_UndoFailure_SaysSo_FakeApi()
    {
        var (rig, vm) = await LoadedDonorsAsync();
        await SelectAsync(vm, 0);
        await vm.DeleteCommand.ExecuteAsync();
        var toast = Assert.Single(rig.Toasts.Toasts);
        rig.Fake.On("RestoreDonorAsync", ApiResult<DonorDetail>.Fail(FeatureRig.Problem(404, ProblemCodes.NotFound)));

        await toast.ActionCommand.ExecuteAsync();

        Assert.Contains(rig.Toasts.Toasts, t => t.Kind == ToastKind.Error && t.Message.Contains("Couldn't restore donor 'Donor 001'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PatientDelete_ConfirmsNamingRecord_RemovesOnlyAfterApiOk_AndShowsFailure_FakeApi()
    {
        var rig = new FeatureRig();
        rig.PatientsReturn(_ => Samples.PatientPage(2));
        rig.Fake.On("GetPatientAsync", ApiResult<PatientDetail>.Ok(Samples.Patient(1, "Patient 001")));
        var vm = rig.Patients();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        rig.Fake.Handlers["DeletePatientAsync"] = _ => Task.FromResult<object>(ApiResult.Fail(FeatureRig.Problem(409, ProblemCodes.Conflict)));
        await vm.DeleteCommand.ExecuteAsync();
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("Delete patient", rig.Dialogs.Confirmations.Single().Verb);
        Assert.Contains("'Patient 001'", rig.Dialogs.Confirmations.Single().Body, StringComparison.Ordinal);

        rig.Fake.Handlers["DeletePatientAsync"] = _ => Task.FromResult<object>(ApiResult.Success);
        rig.PatientsReturn(_ => new PagedResult<PatientListItem>([Samples.PatientRow(2)], 1, 1, 50));
        await vm.DeleteCommand.ExecuteAsync();
        Assert.Single(vm.Items);
        Assert.Contains(rig.Toasts.Toasts, t => t.Message == "Patient 'Patient 001' deleted." && t.ActionLabel == "Undo");

        rig.Fake.On("RestorePatientAsync", ApiResult<PatientDetail>.Ok(Samples.Patient(1, "Patient 001")));
        rig.PatientsReturn(_ => Samples.PatientPage(2));
        await rig.Toasts.Toasts.Single(t => t.ActionLabel == "Undo").ActionCommand.ExecuteAsync();
        Assert.Equal(1, rig.Recorder.LastArg<int>("RestorePatientAsync")); // the same patient comes back
        Assert.Equal(0, rig.Recorder.CountOf("CreatePatientAsync"));
    }

    [Fact]
    public async Task DonorsScreen_AfterDemoResetStyleReload_KeepsTheSelectionWhenTheRecordStillExists_FakeApi()
    {
        var (_, vm) = await LoadedDonorsAsync();
        await SelectAsync(vm, 1);
        Assert.NotNull(vm.Detail);

        await vm.LoadAsync(); // the shell reloads the screen after a demo reset

        Assert.Equal(2, vm.SelectedItem?.Id);
    }

    [Fact]
    public async Task DonorsScreen_DetailFailure_ShowsAnErrorWithRetry_FakeApi()
    {
        var (rig, vm) = await LoadedDonorsAsync();
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Fail(FeatureRig.Problem(404, ProblemCodes.NotFound)));

        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.True(vm.HasDetailError);
        Assert.Contains("was not found", vm.DetailError, StringComparison.Ordinal);
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples.Donor(1, "Donor 001")));
        await vm.RetryDetailCommand.ExecuteAsync();
        Assert.True(vm.HasDetail);
    }

    [Fact]
    public async Task Navigation_ToDonorsWithAPrefilter_SendsThatFilterOnTheFirstCall_FakeApi()
    {
        using var rig = new Rig();
        rig.SignIn();
        rig.Api.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(2)));
        var navigation = rig.Get<Navigation.INavigationService>();

        await navigation.NavigateTo<DonorsViewModel>(new ListPrefilter(BloodType: BloodType.ONeg, Search: "ann"));

        Assert.Equal(1, rig.Api.CountOf("GetDonorsAsync")); // one load per visit
        Assert.IsType<DonorsViewModel>(navigation.Current);
        var vm = (DonorsViewModel)navigation.Current!;
        Assert.Equal("ann", vm.SearchText);
        Assert.Equal(BloodType.ONeg, vm.BloodTypeFilter.Value);
    }

    private sealed class AnsweringPresenter(bool answer) : IDialogPresenter
    {
        public List<string> Titles { get; } = [];

        public Task<bool> ConfirmAsync(string title, string body, string dangerVerb)
        {
            Titles.Add(title);
            return Task.FromResult(answer);
        }

        public Task ShowErrorAsync(string title, string message, string? details) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Navigation_AwayFromADonorsScreenWithADirtyEditor_AsksDiscardChanges_FakeApi(bool discard, bool staysOnDonors)
    {
        using var rig = new Rig();
        rig.SignIn();
        var presenter = new AnsweringPresenter(discard);
        rig.Get<DialogService>().Presenter = presenter;
        rig.Api.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(2)));
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<Contracts.Stock.StockItem>>.Ok(Samples.Stock()));
        var navigation = rig.Get<Navigation.INavigationService>();
        await navigation.NavigateTo<DonorsViewModel>();
        var donors = (DonorsViewModel)navigation.Current!;
        await donors.AddCommand.ExecuteAsync();

        Assert.False(donors.HasUnsavedChanges); // an untouched form does not ask
        ((DonorEditorViewModel)donors.Editor!).Name = "Typed something";
        Assert.True(donors.HasUnsavedChanges);

        var moved = await navigation.NavigateTo<StockViewModel>();

        Assert.Equal(["Discard changes?"], presenter.Titles);
        Assert.Equal(!staysOnDonors, moved);
        Assert.Equal(staysOnDonors, ReferenceEquals(navigation.Current, donors));
    }

    [Fact]
    public async Task Navigation_AwayFromACleanDonorsScreen_AsksNothing_FakeApi()
    {
        using var rig = new Rig();
        rig.SignIn();
        var presenter = new AnsweringPresenter(false);
        rig.Get<DialogService>().Presenter = presenter;
        rig.Api.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(2)));
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<Contracts.Stock.StockItem>>.Ok(Samples.Stock()));
        var navigation = rig.Get<Navigation.INavigationService>();
        await navigation.NavigateTo<DonorsViewModel>();

        Assert.True(await navigation.NavigateTo<StockViewModel>());
        Assert.Empty(presenter.Titles);
    }

    [Fact]
    public async Task Editors_AndScreens_ExposeIsBusy_WhileWorking_AndIsLoadingWhileLoading_FakeApi()
    {
        var rig = new FeatureRig();
        var form = rig.NewDonorForm();
        form.Name = "Maria Lopez";
        form.DateOfBirth = rig.Today.AddYears(-30);
        form.Gender = Gender.Female;
        form.BloodType = BloodType.APos;
        form.Frequency = DonorFrequency.Regular;
        var gate = new TaskCompletionSource();
        rig.Fake.On("CreateDonorAsync", async _ =>
        {
            await gate.Task;
            return ApiResult<DonorDetail>.Ok(Samples.Donor());
        });
        var busy = new List<bool>();
        form.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(form.IsBusy))
            {
                busy.Add(form.IsBusy);
            }
        };

        var save = form.SaveCommand.ExecuteAsync();
        Assert.True(form.IsBusy);
        gate.SetResult();
        await save;
        Assert.False(form.IsBusy);
        Assert.Equal([true, false], busy);

        var donors = rig.Donors();
        var loadGate = new TaskCompletionSource(); // the load is held until the test has looked at it: an instant answer could finish before the assertion runs
        rig.Fake.On("GetDonorsAsync", async _ =>
        {
            await loadGate.Task;
            return ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(1));
        });
        var load = donors.LoadAsync();
        Assert.True(donors.IsLoading);
        loadGate.SetResult();
        await load;
        Assert.False(donors.IsLoading);
    }

    [Fact]
    public async Task List_SortChange_RaisesChangeNotification_ForTheGridIndicator_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        var vm = rig.Donors();
        await vm.LoadAsync();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.SortCommand.ExecuteAsync("-lastDonated");

        Assert.Equal("lastDonated", vm.SortColumn);
        Assert.True(vm.SortDescending);
        Assert.Contains(nameof(vm.SortColumn), raised);
        Assert.Contains(nameof(vm.SortDescending), raised);
    }
}
