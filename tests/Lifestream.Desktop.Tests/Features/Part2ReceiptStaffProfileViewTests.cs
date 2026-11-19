using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Staff;
using Lifestream.Contracts;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Staff;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features;
using Lifestream.Desktop.Features.Profile;
using Lifestream.Desktop.Features.Receipts;
using Lifestream.Desktop.Features.Staff;
using Lifestream.Desktop.Theme;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>Receipts, Staff and Profile rendered off-screen from the fake API (FEAT-17 to FEAT-26). The window is never shown; no print dialog is ever opened.</summary>
[Collection("Wpf")]
public sealed class Part2ReceiptStaffProfileViewTests
{
    private const int Width = 1366;
    private const int Height = 736;

    private sealed class CountingPrinter : IReceiptPrinter
    {
        public int Printed { get; private set; }

        public int Saved { get; private set; }

        public Task<PrintOutcome> PrintAsync(ReceiptDocument document, CancellationToken ct = default)
        {
            Printed++;
            return Task.FromResult(PrintOutcome.Done);
        }

        public Task<PrintOutcome> SaveAsPdfAsync(ReceiptDocument document, CancellationToken ct = default)
        {
            Saved++;
            return Task.FromResult(PrintOutcome.Done);
        }
    }

    private static Button ButtonNamed(Hosted host, string name) => host.All<Button>().First(b => b.IsVisible && AutomationProperties.GetName(b) == name);

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Contrast(Color a, Color b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static Color Token(string key) => (Color)ThemeLoader.Resources[key];

    // ---- Receipts ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ReceiptsView_Loaded_ShowsNamesNotBareIds_Search_AndOnlyTheDateColumnSorts_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var vm = Part2Harness.Create<ReceiptsViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new ReceiptsView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            Hosted.Await(() => { vm.SelectedItem = vm.Items[0]; return vm.PendingDetail; });
            host.Settle(300);
            Part2Harness.Save(host, "ReceiptsView_Loaded.png");

            var grid = host.Find<LifestreamDataGrid>("Grid");
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            return (
                Headers: grid.Columns.Select(c => c.Header?.ToString()).ToList(),
                Sortable: grid.Columns.Where(c => c.CanUserSort).Select(c => c.SortMemberPath).ToList(),
                Keys: vm.SortKeys,
                Staff: texts.Count(t => t.StartsWith("Dr. ", StringComparison.Ordinal)),
                Party: texts.Contains(DemoData.Name(1)),
                Source: texts.Any(t => Regex.IsMatch(t, @"^(Appointment|Request) #\d+$")),
                Search: host.All<TextBox>().Any(t => t.IsVisible && AutomationProperties.GetName(t) == "Search receipts"),
                Add: host.All<Button>().Any(b => b.IsVisible && (AutomationProperties.GetName(b) ?? string.Empty).StartsWith("Add", StringComparison.Ordinal)),
                Open: host.All<Button>().Any(b => b.IsVisible && AutomationProperties.GetName(b) == "Open receipt to print"));
        });

        Assert.Equal(["Date", "Receipt", "Donor or patient", "Blood", "Units", "Issued by"], facts.Headers);
        Assert.Equal(["date"], facts.Sortable);
        Assert.All(facts.Sortable, key => Assert.Contains(key, facts.Keys));
        Assert.True(facts.Staff > 3); // the staff name, with the id only underneath
        Assert.True(facts.Party);
        Assert.True(facts.Source); // "Appointment #301" / "Request #403": the record the receipt belongs to
        Assert.True(facts.Search);
        Assert.False(facts.Add); // receipts are issued, never added
        Assert.True(facts.Open);
    }

    [Fact]
    public void ReceiptsView_Empty_ExplainsHowReceiptsAppear_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            rig.Api.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([], 0, 1, 50)));
            var vm = Part2Harness.Create<ReceiptsViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new ReceiptsView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            Part2Harness.Save(host, "ReceiptsView_Empty.png");
            var empty = Part2Harness.Visible<EmptyState>(host).Single();
            return (empty.Title, empty.Message, empty.ActionText);
        });

        Assert.Equal("No receipts yet", facts.Title);
        Assert.Contains("recorded", facts.Message, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(facts.ActionText)); // nothing to add by hand
    }

    [Fact]
    public void Receipt_PrintViewRenders()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var printer = new CountingPrinter();
            var viewer = new ReceiptViewerViewModel(rig.Api, printer, 1, isDuplicate: true);
            viewer.Attach(null, TimeProvider.System);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new ReceiptViewerView(), viewer), 560, 720);
            host.Settle(150);
            var skeletonWhileLoading = viewer.IsLoading;
            Hosted.Await(() => viewer.LoadAsync());
            host.Settle(500);
            Part2Harness.Save(host, "ReceiptPrint.png");

            var print = host.All<ReceiptPrintView>().Single();
            var text = new TextRange(print.Document!.ContentStart, print.Document.ContentEnd).Text;
            var printButton = ButtonNamed(host, "Print");
            var pdfButton = ButtonNamed(host, "Save as PDF");
            Hosted.Await(() => viewer.PrintCommand.ExecuteAsync());
            Hosted.Await(() => viewer.PrintCommand.ExecuteAsync());
            Hosted.Await(() => viewer.SavePdfCommand.ExecuteAsync());
            host.Settle(200);

            var flow = ReceiptDocumentBuilder.Build(viewer.Document!);
            flow.PageWidth = 794;
            flow.PageHeight = 1123;
            var paginator = ((IDocumentPaginatorSource)flow).DocumentPaginator;
            paginator.PageSize = new Size(794, 1123);
            paginator.ComputePageCount();
            return (skeletonWhileLoading, text, PrintEnabled: printButton.IsEnabled, PdfEnabled: pdfButton.IsEnabled, printer.Printed, printer.Saved, Pages: paginator.PageCount, Black: ((SolidColorBrush)flow.Foreground).Color, White: ((SolidColorBrush)flow.Background).Color);
        });

        Assert.True(facts.skeletonWhileLoading);
        Assert.Contains("Donation receipt #1", facts.text, StringComparison.Ordinal);
        Assert.Contains(DemoData.Name(1), facts.text, StringComparison.Ordinal); // the donor's name
        Assert.Contains("Dr. ", facts.text, StringComparison.Ordinal); // the staff member's name
        Assert.Contains("Duplicate copy printed", facts.text, StringComparison.Ordinal);
        Assert.True(facts.PrintEnabled);
        Assert.True(facts.PdfEnabled);
        Assert.Equal((2, 1), (facts.Printed, facts.Saved)); // a duplicate can be produced again and again
        Assert.True(facts.Pages >= 1);
        Assert.Equal(Colors.Black, facts.Black); // paper: dark text on white
        Assert.Equal(Colors.White, facts.White);
    }

    // ---- Staff ------------------------------------------------------------------------------------------------------

    [Fact]
    public void StaffView_Loaded_NoPasswordColumn_ThumbnailsLoadLazily_RowsWithoutAPhotoShowInitials_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var vm = Part2Harness.Create<StaffViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new StaffView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(600);
            Part2Harness.Save(host, "StaffView_Loaded.png");

            var grid = host.Find<LifestreamDataGrid>("Grid");
            var avatars = host.All<Avatar>().Where(a => a.IsVisible && a.DataContext is StaffRow).ToList();
            var withoutPhoto = avatars.Where(a => !((StaffRow)a.DataContext).HasPhoto).ToList();
            var withPhoto = avatars.Where(a => ((StaffRow)a.DataContext).HasPhoto).ToList();
            return (
                Headers: grid.Columns.Select(c => c.Header?.ToString()).ToList(),
                Sortable: grid.Columns.Count(c => c.CanUserSort),
                PasswordControls: host.All<PasswordField>().Count() + host.All<PasswordBox>().Count(),
                Total: vm.Items.Count,
                RowsWithPhotoTotal: vm.Items.Count(i => i.HasPhoto),
                PhotoCalls: rig.Api.CountOf("GetStaffPhotoAsync"),
                ListCalls: rig.Api.CountOf("GetStaffAsync"),
                // A person without a photo gets their initials, never a drawn or per-user picture.
                InitialsWithoutPhoto: withoutPhoto.Count > 0 && withoutPhoto.All(a => a.Source is null && a.Initials == ((StaffRow)a.DataContext).Initials && a.Initials.Length is 1 or 2),
                LoadedPhotos: vm.Items.Count(i => i.Photo is not null),
                VisibleWithPhoto: withPhoto.Count,
                Count: host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).First(t => t.StartsWith("Showing", StringComparison.Ordinal)));
        });

        Assert.Equal(["Name", "Designation", "Shift", "Role"], facts.Headers); // salary and phone are never in the list payload: they are in the detail pane (admin only)
        Assert.DoesNotContain(facts.Headers, h => h!.Contains("assword", StringComparison.Ordinal));
        Assert.Equal(1, facts.Sortable); // only the name column sorts: the API sorts staff by name or username (Contracts SortKeys)
        Assert.Equal(0, facts.PasswordControls);
        Assert.Equal(1, facts.ListCalls);
        Assert.Equal("Showing 1-50 of 110", facts.Count);
        Assert.True(facts.PhotoCalls > 0);
        Assert.True(facts.PhotoCalls < facts.RowsWithPhotoTotal, $"photo requests {facts.PhotoCalls} must be fewer than the {facts.RowsWithPhotoTotal} rows that have one: only shown rows ask"); // lazy
        Assert.Equal(facts.PhotoCalls, facts.LoadedPhotos);
        Assert.True(facts.InitialsWithoutPhoto); // initials (no image) for every row without a photo
    }

    [Fact]
    public void StaffView_OwnRecord_DeleteIsDisabledWithATooltip_OtherRecordsAreDeletable_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var vm = Part2Harness.Create<StaffViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new StaffView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            rig.Api.On("GetStaffMemberAsync", ApiResult<StaffDetail>.Ok(DemoData2.StaffDetail(1))); // the signed-in admin's own record
            Hosted.Await(() => { vm.SelectedItem = vm.Items[0]; return vm.PendingDetail; });
            host.Settle(400);
            var delete = ButtonNamed(host, "Delete");
            var own = (delete.IsEnabled, Tip: delete.ToolTip as string, Show: ToolTipService.GetShowOnDisabled(delete));
            Part2Harness.Save(host, "StaffView_OwnRecord.png");

            rig.Api.On("GetStaffMemberAsync", ApiResult<StaffDetail>.Ok(DemoData2.StaffDetail(2)));
            Hosted.Await(() => { vm.SelectedItem = vm.Items[1]; return vm.PendingDetail; });
            host.Settle(400);
            var other = ButtonNamed(host, "Delete");
            return (own, Other: (other.IsEnabled, Tip: other.ToolTip), Reset: ButtonNamed(host, "Reset password").IsEnabled);
        });

        Assert.False(facts.own.IsEnabled);
        Assert.Equal("You can't delete your own account.", facts.own.Tip);
        Assert.True(facts.own.Show);
        Assert.True(facts.Other.IsEnabled);
        Assert.Null(facts.Other.Tip);
        Assert.True(facts.Reset);
    }

    [Fact]
    public void StaffView_DemoAccount_ShowsTheListButCannotChangeAnything_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig(Role.Staff, demo: true);
            var vm = Part2Harness.Create<StaffViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new StaffView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() => { vm.SelectedItem = vm.Items[1]; return vm.PendingDetail; });
            host.Settle(400);
            Part2Harness.Save(host, "StaffView_DemoReadOnly.png");
            return (Add: ButtonNamed(host, "Add staff member").IsEnabled, Edit: ButtonNamed(host, "Edit").IsEnabled, Reset: ButtonNamed(host, "Reset password").IsEnabled, Delete: ButtonNamed(host, "Delete").IsEnabled,
                Notice: host.All<TextBlock>().Any(t => t.IsVisible && t.Text.Contains("demo account", StringComparison.Ordinal)));
        });

        Assert.False(facts.Add);
        Assert.False(facts.Edit);
        Assert.False(facts.Reset);
        Assert.False(facts.Delete);
        Assert.True(facts.Notice);
    }

    [Fact]
    public void StaffEditor_New_StartsEmpty_PasswordFieldsAreMaskedWithConfirm_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var vm = Part2Harness.Create<StaffViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new StaffView(), vm), Width, 900);
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() => vm.AddCommand.ExecuteAsync());
            host.Settle(400);
            var form = (StaffEditorViewModel)vm.Editor!;
            Hosted.Await(() => form.SaveCommand.ExecuteAsync());
            host.Settle(300);
            Part2Harness.Save(host, "StaffEditor_New.png");

            var passwords = host.All<PasswordField>().Where(p => p.IsVisible).ToList();
            var date = host.All<DateField>().Single(d => d.IsVisible);
            var salary = host.All<TextBox>().First(t => t.IsVisible && AutomationProperties.GetName(t) == "Salary");
            var textBoxesBoundToPassword = host.All<TextBox>().Count(t => System.Windows.Data.BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path is "Password" or "ConfirmPassword");
            var messages = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            form.Password = TestSecrets.LongEnough;
            host.Settle(100);
            return (
                PasswordFields: passwords.Count,
                Names: passwords.Select(p => AutomationProperties.GetName(p)).ToList(),
                DisplayedWhileMasked: passwords[0].DisplayedText,
                DateText: date.TextBox.Text,
                SalaryText: salary.Text,
                TextBoxesBoundToPassword: textBoxesBoundToPassword,
                Messages: messages);
        });

        Assert.Equal(2, facts.PasswordFields);
        Assert.Equal(["Password", "Confirm password"], facts.Names);
        Assert.Equal(string.Empty, facts.DateText); // no 01 Jan 0001, no made-up date
        Assert.Equal(string.Empty, facts.SalaryText);
        Assert.Equal(0, facts.TextBoxesBoundToPassword); // a password is only ever collected through the masked field
        Assert.Contains("Enter the date of birth.", facts.Messages);
        Assert.Contains("Enter a password.", facts.Messages);
        Assert.Equal(TestSecrets.LongEnough, facts.DisplayedWhileMasked); // the control mirrors its bound value (and its box masks it)
    }

    [Fact]
    public void ResetPasswordDialog_HasTwoMaskedFields_AndNeverShowsAPassword_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var dialog = new ResetPasswordDialogViewModel(rig.Api, 2, DemoData.Name(9));
            using var host = Hosted.Create(Part2Harness.OnCanvas(new ResetPasswordDialogView(), dialog), 560, 460);
            host.Settle(300);
            Hosted.Await(() => dialog.PrimaryCommand.ExecuteAsync()); // empty: messages, nothing sent
            host.Settle(300);
            Part2Harness.Save(host, "ResetPasswordDialog.png");
            return (
                Fields: host.All<PasswordField>().Count(p => p.IsVisible),
                Plain: host.All<TextBox>().Count(t => t.IsVisible),
                Buttons: host.All<Button>().Where(b => b.IsVisible).Select(b => AutomationProperties.GetName(b)).ToList(),
                Texts: host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList(),
                Calls: rig.Api.CountOf("ResetStaffPasswordAsync"));
        });

        Assert.Equal(2, facts.Fields);
        Assert.Equal(0, facts.Plain); // no free-text box in the dialog at all
        Assert.Contains("Reset password", facts.Buttons);
        Assert.Contains("Cancel", facts.Buttons);
        Assert.Contains("Enter a temporary password.", facts.Texts);
        Assert.Equal(0, facts.Calls);
    }

    [Fact]
    public void DigitsOnly_RefusesLettersSignsAndSpaces_ButKeepsDigits()
    {
        var facts = StaHost.Run(() =>
        {
            var box = new TextBox();
            DigitsOnly.SetIsEnabled(box, true);
            bool Raise(string text)
            {
                var e = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, box, text)) { RoutedEvent = UIElement.PreviewTextInputEvent };
                box.RaiseEvent(e);
                return e.Handled;
            }

            return (Letter: Raise("a"), Sign: Raise("-"), Comma: Raise(","), Digit: Raise("7"), Pasted: DigitsOnly.Filter("3,0a0 0"), Empty: DigitsOnly.Filter(null));
        });

        Assert.True(facts.Letter);
        Assert.True(facts.Sign);
        Assert.True(facts.Comma);
        Assert.False(facts.Digit);
        Assert.Equal("3000", facts.Pasted);
        Assert.Equal(string.Empty, facts.Empty);
    }

    // ---- Profile ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ProfileView_Loaded_ReadOnlyDataIsLabelAndValueText_AtContrast_SaveEnabledOnlyWhenChanged_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var vm = Part2Harness.Create<ProfileViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new ProfileView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(500);
            Part2Harness.Save(host, "ProfileView_Loaded.png");

            var save = ButtonNamed(host, "Save changes");
            var before = save.IsEnabled;
            vm.DisplayName = "Test User Changed";
            host.Settle(150);
            var after = save.IsEnabled;
            Part2Harness.Save(host, "ProfileView_Changed.png");

            var username = host.Find<TextBlock>("UsernameValue");
            var designation = host.Find<TextBlock>("DesignationValue");
            var labels = host.All<TextBlock>().Where(t => t.IsVisible && t.Text is "Username" or "Designation" or "Shift").ToList();
            var raised = Token("Color.SurfaceRaised");
            return (
                before, after,
                Values: (username.Text, designation.Text, host.Find<TextBlock>("ShiftValue").Text),
                ReadOnlyAreTextBoxes: host.All<TextBox>().Any(t => t.Name is "UsernameValue" or "DesignationValue" or "ShiftValue"),
                ValueContrast: Contrast(((SolidColorBrush)username.Foreground).Color, raised),
                LabelContrast: labels.Select(l => Contrast(((SolidColorBrush)l.Foreground).Color, raised)).DefaultIfEmpty(0).Min(),
                LabelCount: labels.Count,
                Buttons: host.All<Button>().Where(b => b.IsVisible).Select(b => AutomationProperties.GetName(b)).ToList(),
                Calls: (rig.Api.CountOf("GetProfileAsync"), rig.Api.CountOf("GetMyPhotoAsync")));
        });

        Assert.False(facts.before);
        Assert.True(facts.after);
        Assert.Equal(("tuser", "Admin", "Full time"), facts.Values);
        Assert.False(facts.ReadOnlyAreTextBoxes);
        Assert.True(facts.ValueContrast >= 4.5, $"value contrast {facts.ValueContrast:F2}");
        Assert.Equal(3, facts.LabelCount);
        Assert.True(facts.LabelContrast >= 4.5, $"label contrast {facts.LabelContrast:F2}");
        Assert.Contains("Change password", facts.Buttons);
        Assert.Contains("Change photo", facts.Buttons);
        Assert.Equal((1, 1), facts.Calls);
    }

    [Fact]
    public void ProfileView_Skeleton_ShowsInPlace_AndWithoutAPhotoMakesNoPhotoCall_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var gate = new TaskCompletionSource();
            rig.Api.On("GetProfileAsync", async _ =>
            {
                await gate.Task;
                return ApiResult<Lifestream.Contracts.Profile.ProfileResponse>.Ok(DemoData2.Profile(hasPhoto: false));
            });
            var vm = Part2Harness.Create<ProfileViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new ProfileView(), vm), Width, Height);
            var load = vm.LoadAsync();
            host.Settle(250);
            Part2Harness.Save(host, "ProfileView_Skeleton.png");
            var skeleton = host.All<Skeleton>().Any(s => s.IsVisible);
            gate.SetResult();
            Hosted.Await(() => load);
            host.Settle(400);
            Part2Harness.Save(host, "ProfileView_NoPhoto.png");
            var avatar = host.All<Avatar>().Single(a => a.IsVisible && a.Name == "ProfileAvatar");
            return (skeleton, PhotoCalls: rig.Api.CountOf("GetMyPhotoAsync"), InitialsShown: avatar.Source is null && avatar.Initials == vm.Initials && avatar.Initials.Length is 1 or 2);
        });

        Assert.True(facts.skeleton);
        Assert.Equal(0, facts.PhotoCalls);
        Assert.True(facts.InitialsShown); // the person's initials until they add a photo, never a made-up picture
    }

    [Fact]
    public void ChangePasswordDialog_HasThreeMaskedFields_AndNoPhotoControls_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var dialog = new ChangePasswordDialogViewModel(rig.Api);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new ChangePasswordDialogView(), dialog), 560, 560);
            host.Settle(300);
            Hosted.Await(() => dialog.PrimaryCommand.ExecuteAsync());
            host.Settle(300);
            Part2Harness.Save(host, "ChangePasswordDialog.png");
            return (
                Fields: host.All<PasswordField>().Where(p => p.IsVisible).Select(p => AutomationProperties.GetName(p)).ToList(),
                Plain: host.All<TextBox>().Count(t => t.IsVisible),
                Texts: host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList(),
                Calls: rig.Api.CountOf("ChangePasswordAsync") + rig.Api.CountOf("SetMyPhotoAsync") + rig.Api.CountOf("GetMyPhotoAsync"));
        });

        Assert.Equal(["Current password", "New password", "Confirm new password"], facts.Fields);
        Assert.Equal(0, facts.Plain);
        Assert.Contains("Enter your current password.", facts.Texts);
        Assert.Equal(0, facts.Calls);
    }
}
