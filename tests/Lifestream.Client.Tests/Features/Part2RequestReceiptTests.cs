using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>Requests (create, edit, fulfil, cancel; FEAT-14 to FEAT-16) and Receipts (list, search, printable detail; FEAT-17, FEAT-18), against the fake API client.</summary>
public sealed class Part2RequestReceiptTests
{
    private static async Task<(Part2Rig Rig, RequestsViewModel Vm)> LoadedRequestsAsync(params RequestListItem[] rows)
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new(rows, rows.Length, 1, 50)));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7, 3, "Omar Haddad")));
        var vm = rig.Requests();
        await vm.LoadAsync();
        return (rig, vm);
    }

    private static async Task SelectFirstAsync(RequestsViewModel vm)
    {
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;
    }

    // FEAT-15

    [Fact]
    public async Task RequestsList_FilterByPatientNameOrBloodType_OneApiCallEach_AndShowsThePatientName()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(7, 3, "Omar Haddad")], 1, 1, 50)));
        var vm = rig.Requests();
        await vm.LoadAsync();
        Assert.Equal(1, rig.Recorder.CountOf("GetRequestsAsync"));

        vm.SearchText = "Haddad";
        await vm.SearchNowCommand.ExecuteAsync();
        var byName = rig.Recorder.LastArg<RequestQuery>("GetRequestsAsync");
        Assert.Equal("Haddad", byName.Search); // the patient's name goes to the API: no per-row lookups
        Assert.Equal(2, rig.Recorder.CountOf("GetRequestsAsync")); // one call for the search

        vm.BloodTypeFilter = vm.BloodTypeOptions.First(o => o.Value == BloodType.ANeg);
        await vm.PendingRefresh;
        var byType = rig.Recorder.LastArg<RequestQuery>("GetRequestsAsync");
        Assert.Equal(BloodType.ANeg, byType.BloodType);
        Assert.Equal("Haddad", byType.Search);
        Assert.Equal(3, rig.Recorder.CountOf("GetRequestsAsync")); // and one for the filter

        Assert.Equal("Omar Haddad", vm.Items[0].PatientName); // the column that explains the match
        Assert.Equal(0, rig.Recorder.CountOf("GetPatientAsync") + rig.Recorder.CountOf("GetPatientsAsync"));
    }

    [Fact]
    public async Task RequestsList_RowOpensTheReferencedPatient_AndStatusPrefilterIsSent()
    {
        var (rig, vm) = await LoadedRequestsAsync(Samples2.RequestRow(7, 3, "Omar Haddad"));
        await vm.OpenPatientCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal(typeof(PatientsViewModel), rig.Navigation.Visits.Single().ViewModel);
        Assert.Equal(3, ((ListPrefilter)rig.Navigation.Visits.Single().Parameter!).RecordId);
        Assert.Null(((ListPrefilter)rig.Navigation.Visits.Single().Parameter!).Search);

        var pending = new Part2Rig();
        pending.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([], 0, 1, 50)));
        var second = pending.Requests();
        second.ReceiveParameter(new ListPrefilter(Status: "Pending"));
        await second.LoadAsync();
        Assert.Equal(RequestStatus.Pending, pending.Recorder.LastArg<RequestQuery>("GetRequestsAsync").Status);
    }

    // FEAT-16

    [Fact]
    public async Task FulfilRequest_IsAnActionOnTheRequest_OpensItsStepInTheSidePanel_IssuesReceipt()
    {
        var (rig, vm) = await LoadedRequestsAsync(Samples2.RequestRow(7, 3, "Omar Haddad"));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 8, 0, 10, true)]));
        rig.Fake.On("FulfilRequestAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(92, ReceiptKind.Issue)));
        Assert.False(vm.FulfilCommand.CanExecute(null)); // nothing selected
        await SelectFirstAsync(vm);
        Assert.True(vm.FulfilCommand.CanExecute(null));

        // The fulfil step opens in the side panel (direction A), not as a dialog: it is the view model's Fulfilment until it closes.
        var run = vm.FulfilCommand.ExecuteAsync();
        var fulfil = Assert.IsType<FulfilRequestDialogViewModel>(vm.Fulfilment);
        Assert.True(vm.IsFulfilling);
        Assert.Empty(rig.Forms.Shown); // no dialog
        await fulfil.PendingStock;
        Assert.Equal("In stock: 8 units of A-", fulfil.StockText);
        Assert.False(fulfil.StockIsShort);
        await fulfil.PrimaryCommand.ExecuteAsync();
        await run;
        Assert.Null(vm.Fulfilment); // the step closed after the request was fulfilled

        Assert.Equal(7, rig.Recorder.Calls.Single(c => c.Name == "FulfilRequestAsync").Args[0]);
        var toast = Assert.Single(rig.Toasts.Toasts);
        Assert.Contains("Omar Haddad", toast.Message, StringComparison.Ordinal);
        Assert.Contains("RC-0092", toast.Message, StringComparison.Ordinal);
        Assert.Equal(2, rig.Recorder.CountOf("GetRequestsAsync")); // refreshed
        await toast.ActionCommand.ExecuteAsync();
        Assert.Equal([(92, false)], rig.Viewer.Opened);
    }

    [Fact]
    public async Task FulfilRequest_InsufficientStock_ShowsMessage_NoChange()
    {
        var (rig, vm) = await LoadedRequestsAsync(Samples2.RequestRow(7, 3, "Omar Haddad"));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 1, 0, 10, true)]));
        rig.Fake.On("FulfilRequestAsync", FakeApiClient.Fail<ReceiptDetail>(409, ProblemCodes.InsufficientStock, "Not enough stock"));
        await SelectFirstAsync(vm);
        var run = vm.FulfilCommand.ExecuteAsync();
        var shown = Assert.IsType<FulfilRequestDialogViewModel>(vm.Fulfilment);
        await shown.PendingStock;
        Assert.True(shown.StockIsShort); // the warning is there before the click
        await shown.PrimaryCommand.ExecuteAsync();

        Assert.False(shown.Completed);
        Assert.Equal("There isn't enough A- blood in stock: this request needs 3 units. Nothing was issued.", shown.FormError);
        Assert.False(shown.IsBusy);
        Assert.Same(shown, vm.Fulfilment); // the step stays open with the message
        Assert.False(run.IsCompleted);
        await shown.CancelCommand.ExecuteAsync(); // the user leaves it
        await run;
        Assert.Null(vm.Fulfilment);
        Assert.Empty(rig.Toasts.Toasts); // no success toast
        Assert.Equal(1, rig.Recorder.CountOf("GetRequestsAsync")); // the list was not touched
        Assert.Empty(rig.Viewer.Opened);
    }

    [Fact]
    public async Task CancelRequest_Confirms_ThenCancelsOnlyWhenTheApiSaysOk()
    {
        var (rig, vm) = await LoadedRequestsAsync(Samples2.RequestRow(7, 3, "Omar Haddad"));
        rig.Fake.OnVoid("CancelRequestAsync", ApiResult.Fail(new ApiProblem(409, ProblemCodes.Conflict, "In use")));
        await SelectFirstAsync(vm);

        await vm.CancelRequestCommand.ExecuteAsync();

        Assert.Contains("Omar Haddad", rig.Dialogs.Confirmations.Single().Body, StringComparison.Ordinal);
        Assert.Equal(ToastKind.Error, Assert.Single(rig.Toasts.Toasts).Kind);
        Assert.Equal(1, rig.Recorder.CountOf("GetRequestsAsync")); // the list stays as it was after a refusal

        rig.Fake.OnVoid("CancelRequestAsync", ApiResult.Success);
        await vm.CancelRequestCommand.ExecuteAsync();
        Assert.Equal(2, rig.Recorder.CountOf("GetRequestsAsync"));
    }

    [Fact]
    public void RequestEditor_NewStartsEmpty_ValidatesEverything_NothingSentWhenInvalid()
    {
        var rig = new Part2Rig();
        var form = RequestEditorViewModel.ForNew(rig.Api, rig.Toasts, rig.Dialogs, rig.Clock);

        Assert.Null(form.PatientOption);
        Assert.Equal(string.Empty, form.UnitsText);
        Assert.Null(form.RequiredBy);
        Assert.False(form.HasUnsavedChanges);
        Assert.False(form.ValidateAll());
        Assert.Equal("Choose a patient.", form.GetError(nameof(RequestEditorViewModel.PatientId)));
        Assert.Contains("1 to 10", form.GetError(nameof(RequestEditorViewModel.UnitsText))!, StringComparison.Ordinal);

        form.PatientOption = new RecordOption(3, "Omar Haddad", "A-");
        form.UnitsText = "11";
        form.RequiredBy = rig.Today.AddDays(-1);
        Assert.False(form.ValidateAll());
        Assert.Contains("whole number", form.GetError(nameof(RequestEditorViewModel.UnitsText))!, StringComparison.Ordinal);
        Assert.Contains("past", form.GetError(nameof(RequestEditorViewModel.RequiredBy))!, StringComparison.Ordinal);

        form.UnitsText = "2";
        form.RequiredBy = rig.Today.AddDays(3);
        Assert.True(form.ValidateAll());
        Assert.Equal(new CreateRequestRequest(3, 2, rig.Today.AddDays(3)), new CreateRequestRequest(form.PatientId!.Value, 2, form.RequiredBy.Value));
    }

    [Fact]
    public async Task RequestEditor_Edit_SendsOnlyWhatChanged()
    {
        var rig = new Part2Rig();
        rig.Fake.On("UpdateRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request()));
        var form = RequestEditorViewModel.ForEdit(rig.Api, rig.Toasts, rig.Dialogs, Samples2.Request(), rig.Clock);
        Assert.False(form.HasUnsavedChanges);

        form.UnitsText = "5";
        Assert.True(form.HasUnsavedChanges);
        await form.SaveCommand.ExecuteAsync();

        var sent = rig.Recorder.LastArg<UpdateRequestRequest>("UpdateRequestAsync");
        Assert.Equal("rv-r1", sent.RowVersion);
        Assert.True(sent.Units.HasValue);
        Assert.Equal(5, sent.Units.Value);
        Assert.False(sent.RequiredBy.HasValue); // unchanged fields are absent, not resent
    }

    // FEAT-17, FEAT-18

    [Fact]
    public async Task Receipts_SearchAndNamesNotBareIds()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([Samples2.ReceiptRow(90), Samples2.ReceiptRow(91, ReceiptKind.Issue, "Omar Haddad")], 2, 1, 50)));
        var vm = rig.Receipts();
        await vm.LoadAsync();

        var donation = vm.Items[0];
        Assert.Equal("Dr. Staff Member", donation.StaffName); // the staff name, with the id only as secondary text
        Assert.Equal("S-0001", donation.StaffIdText);
        Assert.Equal("Ayşe Nur Öztürk", donation.PartyName);
        Assert.Equal("Appointment", donation.SourceKindText);
        Assert.Equal("A-0005", donation.SourceIdText);
        var issue = vm.Items[1];
        Assert.Equal("Request", issue.SourceKindText);
        Assert.Equal("R-0007", issue.SourceIdText);
        Assert.Equal("Issue", issue.KindText);
        Assert.Equal("19 Nov 2026 14:30", donation.WhenText);
        Assert.Equal(("19 Nov 2026", "14:30"), (donation.DateText, donation.TimeText)); // the cell's date line over its time line
        Assert.Equal("RC-0090", donation.CodeText);

        vm.SearchText = "Haddad";
        await vm.SearchNowCommand.ExecuteAsync();
        Assert.Equal("Haddad", rig.Recorder.LastArg<ReceiptQuery>("GetReceiptsAsync").Search);

        vm.KindFilter = vm.KindOptions.First(o => o.Value == ReceiptKind.Issue);
        await vm.PendingRefresh;
        Assert.Equal(ReceiptKind.Issue, rig.Recorder.LastArg<ReceiptQuery>("GetReceiptsAsync").Kind);
        Assert.Null(vm.NewCommand); // receipts are issued, never added
    }

    [Fact]
    public async Task Receipts_RowOpensItsPrintableView_AndTheParty()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([Samples2.ReceiptRow(90), Samples2.ReceiptRow(91, ReceiptKind.Issue, "Omar Haddad")], 2, 1, 50)));
        rig.Fake.On("GetAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment(5, donorId: 42)));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7, patientId: 3)));
        var vm = rig.Receipts();
        await vm.LoadAsync();

        await vm.OpenReceiptCommand.ExecuteAsync(vm.Items[1]);
        Assert.Equal([(91, true)], rig.Viewer.Opened);

        await vm.OpenPartyCommand.ExecuteAsync(vm.Items[0]);
        await vm.OpenPartyCommand.ExecuteAsync(vm.Items[1]);
        Assert.Equal(typeof(DonorsViewModel), rig.Navigation.Visits[0].ViewModel);
        Assert.Equal(42, ((ListPrefilter)rig.Navigation.Visits[0].Parameter!).RecordId);
        Assert.Equal(typeof(PatientsViewModel), rig.Navigation.Visits[1].ViewModel);
        Assert.Equal(3, ((ListPrefilter)rig.Navigation.Visits[1].Parameter!).RecordId);
    }

    [Fact]
    public void ReceiptDocument_NamesStaffPartyAndSource_NotBareIds()
    {
        var donation = ReceiptDocument.From(Samples2.Receipt(90), isDuplicate: false, zone: TimeZoneInfo.Utc);
        Assert.Equal("Donation receipt", donation.Title);
        Assert.Contains(donation.Lines, l => l is { Label: "Donor", Value: "Ayşe Nur Öztürk-Abdulrahman" });
        Assert.Contains(donation.Lines, l => l is { Label: "Appointment" } && l.Value.StartsWith("Ayşe", StringComparison.Ordinal));
        Assert.Contains(donation.Lines, l => l is { Label: "Issued by", Value: "Dr. Staff Member (S-0001)" });
        Assert.Contains(donation.Lines, l => l is { Label: "Date", Value: "19 Nov 2026 14:30" });
        Assert.Null(donation.CopyNotice);

        var issue = ReceiptDocument.From(Samples2.Receipt(91, ReceiptKind.Issue), isDuplicate: true, zone: TimeZoneInfo.Utc);
        Assert.Equal("Blood issue receipt", issue.Title);
        Assert.Contains(issue.Lines, l => l.Label == "Patient");
        Assert.Contains(issue.Lines, l => l is { Label: "Request" });
        Assert.StartsWith("Duplicate copy printed", issue.CopyNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receipt_Viewer_PrintAndSaveAsPdf_CanBeRepeated_ADuplicateIsAlwaysProducible()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(90)));
        var viewer = new ReceiptViewerViewModel(rig.Api, rig.Printer, 90, isDuplicate: true);
        viewer.Attach(null, rig.Clock);
        Assert.False(viewer.PrintCommand.CanExecute(null)); // nothing to print until it has loaded
        await viewer.LoadAsync();
        Assert.True(viewer.PrintCommand.CanExecute(null));

        await viewer.PrintCommand.ExecuteAsync();
        await viewer.PrintCommand.ExecuteAsync();
        await viewer.SavePdfCommand.ExecuteAsync();
        await viewer.SavePdfCommand.ExecuteAsync();

        Assert.Equal(2, rig.Printer.Printed.Count);
        Assert.Equal(2, rig.Printer.Saved.Count);
        Assert.All(rig.Printer.Printed, d => Assert.NotNull(d.CopyNotice));
        Assert.Equal("Sent to the PDF printer. Choose where to save the file.", viewer.Notice);
        Assert.Equal(1, rig.Recorder.CountOf("GetReceiptAsync")); // printing again never asks the server again
    }

    [Fact]
    public async Task Receipt_Viewer_NoPrinterAndCancelledDialog_AreSaidInWordsOrSilent()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(90)));
        var viewer = new ReceiptViewerViewModel(rig.Api, rig.Printer, 90, isDuplicate: false);
        viewer.Attach(null, rig.Clock);
        await viewer.LoadAsync();

        rig.Printer.Outcome = PrintOutcome.Cancelled;
        await viewer.PrintCommand.ExecuteAsync();
        Assert.False(viewer.HasFormError);
        Assert.False(viewer.HasNotice);

        rig.Printer.Outcome = PrintOutcome.Unavailable;
        await viewer.SavePdfCommand.ExecuteAsync();
        Assert.Contains("Microsoft Print to PDF", viewer.FormError, StringComparison.Ordinal);
        Assert.False(viewer.IsBusy);
    }

    [Fact]
    public async Task Receipt_Viewer_LoadFailure_OffersRetry()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptAsync", FakeApiClient.Unreachable<ReceiptDetail>());
        var viewer = new ReceiptViewerViewModel(rig.Api, rig.Printer, 90, isDuplicate: false);
        viewer.Attach(null, rig.Clock);

        await viewer.LoadAsync();

        Assert.True(viewer.HasError);
        Assert.False(viewer.PrintCommand.CanExecute(null));
        rig.Fake.On("GetReceiptAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(90)));
        await viewer.RetryCommand.ExecuteAsync();
        Assert.True(viewer.IsReady);
        Assert.NotNull(viewer.Document);
    }

    [Fact]
    public async Task ReceiptViewerService_ShowsTheViewerOnce_AndLoadsOneReceipt()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(90)));
        var service = new ReceiptViewerService(rig.Api, rig.Printer, rig.Forms);
        rig.Forms.Script = viewer => Task.Run(() => SpinWait.SpinUntil(() => ((ReceiptViewerViewModel)viewer).IsReady, TimeSpan.FromSeconds(5))); // the "user" looks at the receipt once it has loaded

        await service.ShowAsync(90, isDuplicate: true);

        var shown = Assert.IsType<ReceiptViewerViewModel>(Assert.Single(rig.Forms.Shown));
        Assert.Equal(90, shown.ReceiptId);
        Assert.Equal(1, rig.Recorder.CountOf("GetReceiptAsync"));
    }
}
