using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Requests;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Requests;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Appointments;
using Lifestream.Desktop.Features.Requests;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>Appointments and Requests rendered off-screen from the fake API (FEAT-09 to FEAT-16). The window is never shown; captures go to the task's captures folder.</summary>
[Collection("Wpf")]
public sealed class Part2AppointmentRequestViewTests
{
    private const int Width = 1366;
    private const int Height = 736;

    private static Button ButtonNamed(Hosted host, string name) => host.All<Button>().First(b => b.IsVisible && AutomationProperties.GetName(b) == name);

    private static Task SelectFirst<TItem, TDetail>(Lifestream.Client.Features.ListScreens.RecordListViewModel<TItem, TDetail> vm)
        where TItem : class
        where TDetail : class
    {
        vm.SelectedItem = vm.Items[0];
        return vm.PendingDetail;
    }

    // ---- Appointments -----------------------------------------------------------------------------------------------

    [Fact]
    public void AppointmentsView_Loaded_ShowsNamesWithIdsAsSecondaryText_StatusChips_AndOnlyTheTimeColumnSorts_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var vm = Part2Harness.Create<AppointmentsViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new AppointmentsView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            Hosted.Await(() => SelectFirst(vm));
            host.Settle(300);
            Part2Harness.Save(host, "AppointmentsView_Loaded.png");

            var grid = host.Find<LifestreamDataGrid>("Grid");
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            var chips = Part2Harness.Visible<StatusChip>(host).ToList();
            return (
                Count: texts.First(t => t.StartsWith("Showing", StringComparison.Ordinal)),
                Headers: grid.Columns.Select(c => c.Header?.ToString()).ToList(),
                Sortable: grid.Columns.Where(c => c.CanUserSort).Select(c => c.SortMemberPath).ToList(),
                VmKeys: vm.SortKeys,
                DonorIds: texts.Count(t => Regex.IsMatch(t, @"^D-01\d\d$")),
                Names: texts.Contains(DemoData.Name(1)),
                ChipCount: chips.Count,
                ChipTexts: chips.Select(c => c.DisplayText).Distinct().Order().ToList(),
                LocalTimes: texts.Any(t => Regex.IsMatch(t, @"^\d{2}:\d{2}$")),
                DayHeadings: texts.Count(t => Regex.IsMatch(t, @"^((Today|Tomorrow|Yesterday), )?[A-Z][a-z]{2} \d{2} [A-Z][a-z]{2}( \d{4})?$")),
                Actions: new[] { "Record donation", "Reschedule", "Cancel appointment" }.Select(n => host.All<Button>().Any(b => b.IsVisible && AutomationProperties.GetName(b) == n)).ToList(),
                Skeletons: host.All<Skeleton>().Count(s => s.IsVisible),
                Calls: (rig.Api.CountOf("GetAppointmentsAsync"), rig.Api.CountOf("GetAppointmentAsync")));
        });

        Assert.Equal("Showing 1-50 of 86", facts.Count);
        Assert.Equal(["Time", "Donor", "Group", "Status", "Booked on"], facts.Headers); // no id column, no constant or always-empty column; the day is the group heading
        Assert.Equal(["slot"], facts.Sortable);
        Assert.All(facts.Sortable, key => Assert.Contains(key, facts.VmKeys)); // a column only sorts by a key the screen declares (and the API accepts)
        Assert.True(facts.DonorIds > 3); // "D-0101": the donor's code, as secondary text under the name
        Assert.True(facts.Names);
        Assert.True(facts.ChipCount > 3);
        Assert.Contains("Pending", facts.ChipTexts);
        Assert.Contains("Cancelled", facts.ChipTexts);
        Assert.Contains("Completed", facts.ChipTexts);
        Assert.Contains("No show", facts.ChipTexts);
        Assert.True(facts.LocalTimes); // "14:00": the local time of day in the row
        Assert.True(facts.DayHeadings > 0); // and the local day as the heading of its group
        Assert.All(facts.Actions, Assert.True);
        Assert.Equal(0, facts.Skeletons);
        Assert.Equal((1, 1), facts.Calls);
    }

    [Fact]
    public void AppointmentsView_Skeleton_AndEmpty_AndError_AreShownInPlace_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var gate = new TaskCompletionSource();
            rig.Api.On("GetAppointmentsAsync", async _ =>
            {
                await gate.Task;
                return ApiResult<PagedResult<AppointmentListItem>>.Ok(new([], 0, 1, 50));
            });
            var vm = Part2Harness.Create<AppointmentsViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new AppointmentsView(), vm), Width, Height);
            var load = vm.LoadAsync();
            host.Settle(250);
            Part2Harness.Save(host, "AppointmentsView_Skeleton.png");
            var skeletonShown = host.All<Skeleton>().Any(s => s.IsVisible);
            var gridShown = host.Find<LifestreamDataGrid>("Grid").IsVisible;
            gate.SetResult();
            Hosted.Await(() => load);
            host.Settle(300);
            Part2Harness.Save(host, "AppointmentsView_Empty.png");
            var empty = Part2Harness.Visible<EmptyState>(host).Single();

            rig.Api.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Fail(new ApiProblem(503, ProblemCodes.DatabaseWaking, "Waking", "corr-1")));
            Hosted.Await(() => vm.RetryCommand.ExecuteAsync());
            host.Settle(300);
            Part2Harness.Save(host, "AppointmentsView_Error.png");
            return (skeletonShown, gridShown, empty.Title, empty.ActionText, ErrorShown: Part2Harness.Visible<ErrorState>(host).Count());
        });

        Assert.True(facts.skeletonShown);
        Assert.False(facts.gridShown);
        Assert.Equal("No appointments yet", facts.Title);
        Assert.Equal(1, facts.ErrorShown);
    }

    [Fact]
    public void AppointmentBookingForm_ShowsDatePicker_SlotsWithTakenDisabled_BannerAndReasonForIneligibleDonor_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            rig.Api.On("GetDonorAsync", ApiResult<Lifestream.Contracts.Donors.DonorDetail>.Ok(DemoData2.Donor(101, recentlyDonated: true)));
            var vm = Part2Harness.Create<AppointmentsViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new AppointmentsView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() => vm.AddCommand.ExecuteAsync());
            host.Settle(400);
            var form = (AppointmentEditorViewModel)vm.Editor!;
            // Set from inside the dispatcher, as a click would: the loads then continue on the UI thread.
            Hosted.Await(() =>
            {
                form.DonorOption = new RecordOption(101, DemoData.Name(1), "O+");
                return form.PendingDonor;
            });
            Hosted.Await(() =>
            {
                form.Date = DemoData2.BookingDay;
                return form.PendingSlots;
            });
            host.Settle(400);
            Part2Harness.Save(host, "AppointmentBookingForm.png");

            var dateField = host.All<DateField>().Single(d => d.IsVisible);
            var list = host.All<ListBox>().Single(l => AutomationProperties.GetName(l) == "Available times");
            var items = Enumerable.Range(0, list.Items.Count).Select(i => (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i)).ToList();
            var banner = Part2Harness.Visible<EligibilityBannerView>(host).Single();
            var bannerTexts = Hosted.Descendants(banner).OfType<TextBlock>().Select(t => t.Text).ToList();
            var reasonBox = host.All<TextBox>().FirstOrDefault(t => AutomationProperties.GetName(t) == "Override reason");
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            return (
                Min: dateField.MinDate,
                Earliest: form.EarliestDate,
                SlotCount: items.Count,
                Taken: items.Count(i => i is not null && !i.IsEnabled),
                TakenLabelled: items.Count(i => i is not null && i.Content is SlotChoice { IsTaken: true } slot && slot.Label.EndsWith("(taken)", StringComparison.Ordinal)),
                BannerTexts: bannerTexts,
                ReasonShown: reasonBox?.IsVisible == true,
                Title: texts.Contains("Book appointment"),
                SaveText: ButtonNamed(host, "Book appointment").Content);
        });

        Assert.Equal(facts.Earliest, facts.Min); // the date picker blocks past dates
        Assert.Equal(16, facts.SlotCount);
        Assert.Equal(3, facts.Taken); // taken times are disabled...
        Assert.Equal(3, facts.TakenLabelled); // ...and say so in words
        Assert.Contains("Not eligible to donate", facts.BannerTexts);
        Assert.Contains(facts.BannerTexts, t => t.StartsWith("Age: ", StringComparison.Ordinal));
        Assert.Contains(facts.BannerTexts, t => t.StartsWith("Last donation: ", StringComparison.Ordinal));
        Assert.True(facts.ReasonShown);
        Assert.True(facts.Title);
    }

    [Fact]
    public void AppointmentBookingForm_EligibleDonor_HidesTheReasonField_AndEmptyFormShowsMessages_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var vm = Part2Harness.Create<AppointmentsViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new AppointmentsView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() => vm.AddCommand.ExecuteAsync());
            host.Settle(300);
            var form = (AppointmentEditorViewModel)vm.Editor!;

            Hosted.Await(() => form.SaveCommand.ExecuteAsync()); // nothing chosen at all: no crash, messages under the fields
            host.Settle(300);
            Part2Harness.Save(host, "AppointmentBookingForm_Empty.png");
            var messages = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();

            Hosted.Await(() =>
            {
                form.DonorOption = new RecordOption(101, DemoData.Name(1), "O+");
                return form.PendingDonor;
            });
            host.Settle(200);
            return (messages, Reason: host.All<TextBox>().Any(t => t.IsVisible && AutomationProperties.GetName(t) == "Override reason"), Banner: form.Banner.State, Booked: rig.Api.CountOf("BookAppointmentAsync"));
        });

        Assert.Contains("Choose a donor.", facts.messages);
        Assert.Contains("Choose a date.", facts.messages);
        Assert.Contains("Choose a time.", facts.messages);
        Assert.False(facts.Reason);
        Assert.Equal(BannerState.Eligible, facts.Banner);
        Assert.Equal(0, facts.Booked);
    }

    [Fact]
    public void RecordDonationDialog_ShowsDonorFromTheAppointment_Banner_UnitsAndNotes_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            rig.Api.On("GetDonorAsync", ApiResult<Lifestream.Contracts.Donors.DonorDetail>.Ok(DemoData2.Donor(101, recentlyDonated: true)));
            rig.Api.On("GetDonorEligibilityAsync", ApiResult<Lifestream.Contracts.Donors.DonorEligibility>.Ok(new(false, ["At least 90 days are needed between donations; next eligible on 14 Jan 2027."], new DateOnly(2027, 1, 14))));
            var dialog = new RecordDonationDialogViewModel(rig.Api, DemoData2.AppointmentDetail(1), TimeProvider.System);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new RecordDonationDialogView(), dialog), 560, 640);
            Hosted.Await(() => dialog.PendingCheck);
            host.Settle(400);
            Part2Harness.Save(host, "RecordDonationDialog.png");

            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            var buttons = host.All<Button>().Where(b => b.IsVisible).Select(b => AutomationProperties.GetName(b)).ToList();
            var reason = host.All<TextBox>().FirstOrDefault(t => AutomationProperties.GetName(t) == "Override reason");
            Hosted.Await(() => dialog.PrimaryCommand.ExecuteAsync());
            host.Settle(300);
            return (texts, buttons, ReasonShown: reason?.IsVisible == true, Error: host.All<FormField>().Where(f => f.IsVisible).Select(f => f.Error).FirstOrDefault(e => !string.IsNullOrEmpty(e)), Calls: rig.Api.CountOf("RecordDonationAsync"));
        });

        Assert.Contains(DemoData.Name(1), facts.texts);
        Assert.Contains("Not eligible to donate", facts.texts);
        Assert.Contains("Record donation", facts.buttons);
        Assert.Contains("Cancel", facts.buttons);
        Assert.True(facts.ReasonShown);
        Assert.Contains("isn't eligible", facts.Error, StringComparison.Ordinal);
        Assert.Equal(0, facts.Calls); // submit is blocked until a reason is entered
    }

    // ---- Requests ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RequestsView_Loaded_ShowsPatientNameColumnWithIds_Chips_AndSortsOnlyByApiKeys_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var vm = Part2Harness.Create<RequestsViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new RequestsView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            Hosted.Await(() => SelectFirst(vm));
            host.Settle(300);
            Part2Harness.Save(host, "RequestsView_Loaded.png");

            var grid = host.Find<LifestreamDataGrid>("Grid");
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            return (
                Count: texts.First(t => t.StartsWith("Showing", StringComparison.Ordinal)),
                Headers: grid.Columns.Select(c => c.Header?.ToString()).ToList(),
                Sortable: grid.Columns.Where(c => c.CanUserSort).Select(c => c.SortMemberPath).ToList(),
                Keys: vm.SortKeys,
                Patient: texts.Contains(DemoData.Name(4)),
                Ids: texts.Count(t => Regex.IsMatch(t, @"^P-02\d\d$")),
                Chips: Part2Harness.Visible<StatusChip>(host).Select(c => c.DisplayText).Distinct().Order().ToList(),
                SearchLabel: InputAssist.GetPlaceholder(host.Find<TextBox>("SearchBox")),
                Fulfil: host.All<Button>().Any(b => b.IsVisible && AutomationProperties.GetName(b) == "Fulfil request"));
        });

        Assert.Equal("Showing 1-40 of 40", facts.Count);
        Assert.Equal(["Patient", "Group", "Units", "Needed by", "Urgency", "Status"], facts.Headers);
        Assert.Equal(["requiredBy"], facts.Sortable);
        Assert.All(facts.Sortable, key => Assert.Contains(key, facts.Keys));
        Assert.True(facts.Patient);
        Assert.True(facts.Ids > 3);
        Assert.Contains("Pending", facts.Chips);
        Assert.Contains("Fulfilled", facts.Chips);
        Assert.Equal("Search by patient name or number", facts.SearchLabel); // the one search box says what it finds
        Assert.True(facts.Fulfil);
    }

    [Fact]
    public void RequestsView_Skeleton_Empty_AreCaptured_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var gate = new TaskCompletionSource();
            rig.Api.On("GetRequestsAsync", async _ =>
            {
                await gate.Task;
                return ApiResult<PagedResult<RequestListItem>>.Ok(new([], 0, 1, 50));
            });
            var vm = Part2Harness.Create<RequestsViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new RequestsView(), vm), Width, Height);
            var load = vm.LoadAsync();
            host.Settle(250);
            Part2Harness.Save(host, "RequestsView_Skeleton.png");
            var skeleton = host.All<Skeleton>().Any(s => s.IsVisible);
            gate.SetResult();
            Hosted.Await(() => load);
            host.Settle(300);
            Part2Harness.Save(host, "RequestsView_Empty.png");
            return (skeleton, Empty: Part2Harness.Visible<EmptyState>(host).Single().Title);
        });

        Assert.True(facts.skeleton);
        Assert.Equal("No requests yet", facts.Empty);
    }

    [Fact]
    public void RequestForm_NewRequest_ShowsPatientPicker_DigitsOnlyUnits_AndDateField_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var vm = Part2Harness.Create<RequestsViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new RequestsView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() => vm.AddCommand.ExecuteAsync());
            host.Settle(400);
            Hosted.Await(() => ((RequestEditorViewModel)vm.Editor!).SaveCommand.ExecuteAsync());
            host.Settle(300);
            Part2Harness.Save(host, "RequestForm_Empty.png");

            var units = host.All<TextBox>().First(t => t.IsVisible && AutomationProperties.GetName(t) == "Units needed");
            var typed = new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, units, "x"))
            {
                RoutedEvent = UIElement.PreviewTextInputEvent,
            };
            units.RaiseEvent(typed);
            return (
                Picker: host.All<RecordPicker>().Any(p => p.IsVisible),
                Date: host.All<DateField>().Any(d => d.IsVisible),
                LetterBlocked: typed.Handled,
                Messages: host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList(),
                Title: host.All<TextBlock>().Where(t => t.IsVisible).Any(t => t.Text == "New blood request"));
        });

        Assert.True(facts.Picker);
        Assert.True(facts.Date);
        Assert.True(facts.LetterBlocked);
        Assert.Contains("Choose a patient.", facts.Messages);
        Assert.True(facts.Title);
    }

    [Fact]
    public void FulfilRequestDialog_InsufficientStock_ShowsTheWarningAndTheMessage_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<Lifestream.Contracts.Stock.StockItem>>.Ok([new(BloodType.ANeg, 1, 0, 10, true), new(BloodType.APos, 1, 0, 10, true)]));
            rig.Api.On("FulfilRequestAsync", ApiResult<Lifestream.Contracts.Receipts.ReceiptDetail>.Fail(new ApiProblem(409, ProblemCodes.InsufficientStock, "Not enough stock", "corr-2")));
            var request = DemoData2.RequestDetail(1) with { BloodType = BloodType.ANeg, Units = 3 };
            var dialog = new FulfilRequestDialogViewModel(rig.Api, request);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new FulfilRequestDialogView(), dialog), 560, 520);
            Hosted.Await(() => dialog.PendingStock);
            host.Settle(300);
            Hosted.Await(() => dialog.PrimaryCommand.ExecuteAsync());
            Hosted.Await(() => dialog.PendingStock);
            host.Settle(300);
            Part2Harness.Save(host, "FulfilRequestDialog_InsufficientStock.png");
            return (Texts: host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList(), dialog.Completed, Busy: dialog.IsBusy, Primary: ButtonNamed(host, "Fulfil request").IsEnabled);
        });

        Assert.Contains("In stock: 1 unit of A-", facts.Texts);
        Assert.Contains("There isn't enough A- blood in stock: this request needs 3 units. Nothing was issued.", facts.Texts);
        Assert.False(facts.Completed);
        Assert.False(facts.Busy);
        Assert.True(facts.Primary); // the dialog stays usable
    }
}
