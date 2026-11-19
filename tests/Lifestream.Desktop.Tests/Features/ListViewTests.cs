using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Donors;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>Donors and Patients rendered off-screen from the fake API (FEAT-01, FEAT-03, FEAT-04, FEAT-07, FEAT-08). The window is never shown.</summary>
[Collection("Wpf")]
public sealed class ListViewTests
{
    private const int Width = 1366;
    private const int Height = 736; // 768 minus the 32 DIP caption

    /// <summary>Selects the first row from the UI thread's dispatcher, as a click would, and completes when its detail is loaded.</summary>
    private static Task SelectFirst<TItem, TDetail>(Lifestream.Client.Features.ListScreens.RecordListViewModel<TItem, TDetail> vm)
        where TItem : class
        where TDetail : class
    {
        vm.SelectedItem = vm.Items[0];
        return vm.PendingDetail;
    }

    [Fact]
    public void DonorsView_Loaded_ShowsOnePageOfRows_WithCountAndDetailPane_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<DonorsViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.DonorsView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            Hosted.Await(() => SelectFirst(vm));
            host.Settle(300);
            FeatureHarness.Save(host, "DonorsView_Loaded.png");

            var grid = host.Find<LifestreamDataGrid>("Grid");
            var rows = host.All<DataGridRow>().Count();
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            return (
                Count: texts.First(t => t.StartsWith("Showing", StringComparison.Ordinal)),
                Rows: rows,
                Items: grid.Items.Count,
                FirstDob: texts.Contains("17 Mar 1966") || texts.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{2} [A-Z][a-z]{2} \d{4}$")),
                DetailTitle: texts.Contains(vm.DetailTitle),
                Eligible: texts.Contains("Eligible to donate"),
                Skeletons: host.All<Skeleton>().Count(s => s.IsVisible),
                Calls: (rig.Api.CountOf("GetDonorsAsync"), rig.Api.CountOf("GetDonorAsync")));
        });

        Assert.Equal("Showing 1-50 of 128", facts.Count);
        Assert.Equal(50, facts.Items);
        Assert.InRange(facts.Rows, 1, 50);
        Assert.True(facts.FirstDob);
        Assert.True(facts.DetailTitle);
        Assert.True(facts.Eligible);
        Assert.Equal(0, facts.Skeletons);
        Assert.Equal((1, 1), facts.Calls);
    }

    [Fact]
    public void DonorsView_Skeleton_ShowsInPlaceOfTheGrid_WhileLoading_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var gate = new TaskCompletionSource();
            rig.Api.On("GetDonorsAsync", async _ =>
            {
                await gate.Task;
                return ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(5));
            });
            var vm = FeatureHarness.Create<DonorsViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.DonorsView(vm), Width, Height);

            var load = vm.LoadAsync();
            host.Settle(250);
            FeatureHarness.Save(host, "DonorsView_Skeleton.png");
            var grid = host.Find<LifestreamDataGrid>("Grid");
            var skeleton = host.All<Skeleton>().First(s => s.IsVisible);
            var gridBounds = FeatureHarness.BoundsIn(host.Find<LoadSurface>("ListSurface"), host.Element);
            var skeletonBounds = FeatureHarness.BoundsIn(skeleton, host.Element);
            var result = (
                Loading: vm.ShowSkeleton,
                GridVisible: grid.IsVisible,
                Columns: skeleton.ColumnWidths,
                SameCell: Math.Abs(gridBounds.Left - skeletonBounds.Left) < 1 && skeletonBounds.Width > 300);

            gate.SetResult();
            Hosted.Await(() => load);
            host.Settle(250);
            return (result, GridVisibleAfter: grid.IsVisible, SkeletonAfter: host.All<Skeleton>().Count(s => s.IsVisible));
        });

        Assert.True(facts.result.Loading);
        Assert.False(facts.result.GridVisible);
        Assert.Equal("2*,100,90,*,130", facts.result.Columns);
        Assert.True(facts.result.SameCell);
        Assert.True(facts.GridVisibleAfter);
        Assert.Equal(0, facts.SkeletonAfter);
    }

    [Fact]
    public void DonorsView_Empty_ShowsTheEmptyStateWithAnAddAction_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            rig.Api.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(0)));
            var vm = FeatureHarness.Create<DonorsViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.DonorsView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            FeatureHarness.Save(host, "DonorsView_Empty.png");

            var empty = FeatureHarness.Visible<EmptyState>(host).Single();
            var addButtons = host.All<Button>().Where(b => b.IsVisible && b.Command == vm.AddCommand).ToList();
            return (empty.Title, empty.ActionText, Grid: host.Find<LifestreamDataGrid>("Grid").IsVisible, AddButtons: addButtons.Count);
        });

        Assert.Equal("No donors yet", facts.Title);
        Assert.Equal("Add donor", facts.ActionText);
        Assert.False(facts.Grid);
        Assert.True(facts.AddButtons >= 1); // the toolbar's button and the empty state's action
    }

    [Fact]
    public void DonorsView_NoMatches_OffersClearFilters_AndStaysUsable_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<DonorsViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.DonorsView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            rig.Api.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(0)));
            vm.SearchText = "zzz";
            Hosted.Await(() => vm.SearchNowCommand.ExecuteAsync());
            host.Settle(300);

            var empty = FeatureHarness.Visible<EmptyState>(host).Single();
            var clear = FeatureHarness.Visible<Button>(host).Where(b => (string)AutomationProperties.GetName(b) == "Clear filters").ToList();
            var searchBox = host.Find<TextBox>("SearchBox");
            return (empty.Title, empty.ActionText, ClearButtons: clear.Count, SearchVisible: searchBox.IsVisible, SearchText: searchBox.Text);
        });

        Assert.Equal("No donors match these filters", facts.Title);
        Assert.Equal("Clear filters", facts.ActionText);
        Assert.True(facts.ClearButtons >= 1);
        Assert.True(facts.SearchVisible); // the search box is outside the swapped surface, so the user can edit it
        Assert.Equal("zzz", facts.SearchText);
    }

    [Fact]
    public void DonorsView_Error_ShowsMessageDetailsAndRetry_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            rig.Api.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Fail(new ApiProblem(503, ProblemCodes.DatabaseWaking, "Waking", "corr-9")));
            var vm = FeatureHarness.Create<DonorsViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.DonorsView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            FeatureHarness.Save(host, "DonorsView_Error.png");

            var error = FeatureHarness.Visible<ErrorState>(host).Single();
            var retry = FeatureHarness.Visible<Button>(host).Single(b => Equals(b.Content, "Retry"));
            var message = error.Message;
            DemoData.Configure(rig.Api);
            Hosted.Await(() => vm.RetryCommand.ExecuteAsync());
            host.Settle(300);
            return (Message: message, ErrorShown: error.IsVisible, RetryCommandSet: retry.Command is not null, Rows: vm.Items.Count);
        });

        Assert.Contains("still starting up", facts.Message, StringComparison.Ordinal);
        Assert.False(facts.ErrorShown);
        Assert.True(facts.RetryCommandSet);
        Assert.Equal(50, facts.Rows);
    }

    [Fact]
    public void PatientsView_Loaded_ShowsOnePageOfRows_WithCount_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<PatientsViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.PatientsView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            Hosted.Await(() => SelectFirst(vm));
            host.Settle(300);
            FeatureHarness.Save(host, "PatientsView_Loaded.png");
            var texts = host.All<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            return (texts.First(t => t.StartsWith("Showing", StringComparison.Ordinal)), host.All<DataGridRow>().Count(), texts.Contains(vm.DetailTitle));
        });

        Assert.Equal("Showing 1-50 of 64", facts.Item1);
        Assert.True(facts.Item2 > 0);
        Assert.True(facts.Item3);
    }

    [Fact]
    public void DonorsView_PageButtons_AreStateAware_AndSortHeadersAskTheServer_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = FeatureHarness.NewRig();
            var vm = FeatureHarness.Create<DonorsViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.DonorsView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);

            var previous = host.All<Button>().Single(b => AutomationProperties.GetName(b) == "Previous page");
            var next = host.All<Button>().Single(b => AutomationProperties.GetName(b) == "Next page");
            var before = (previous.IsEnabled, next.IsEnabled);

            var grid = host.Find<LifestreamDataGrid>("Grid");
            var nameColumn = grid.Columns.First(c => c.SortMemberPath == "name");
            var phoneColumn = grid.Columns.First(c => c.Header is "Phone");
            return (before, NameSortable: nameColumn.CanUserSort, PhoneSortable: phoneColumn.CanUserSort, Sorts: grid.Columns.Where(c => c.CanUserSort).Select(c => c.SortMemberPath).Where(p => !string.IsNullOrEmpty(p)).ToList());
        });

        Assert.False(facts.before.Item1);
        Assert.True(facts.before.Item2);
        Assert.True(facts.NameSortable);
        Assert.False(facts.PhoneSortable);
        Assert.Equal(["name", "lastDonated"], facts.Sorts); // the keys the API accepts for donors (Contracts SortKeys); blood type no longer sorts
    }

    // The form (FEAT-07, FEAT-08)

    private static (Hosted Host, DonorsViewModel Vm, Rig Rig) OpenNewDonorForm()
    {
        var rig = FeatureHarness.NewRig();
        var vm = FeatureHarness.Create<DonorsViewModel>(rig);
        var host = Hosted.Create(FeatureHarness.DonorsView(vm), Width, Height);
        Hosted.Await(() => vm.LoadAsync());
        Hosted.Await(() => vm.AddCommand.ExecuteAsync());
        host.Settle(400);
        return (host, vm, rig);
    }

    [Fact]
    public void DonorForm_RequiredFieldsAreMarked_TwoColumnLeftAlignedGrid_PrimaryActionBottomRight_CancelEnabled()
    {
        var facts = StaHost.Run(() =>
        {
            var (host, vm, rig) = OpenNewDonorForm();
            using (host)
            using (rig)
            {
                FeatureHarness.Save(host, "DonorForm_New.png");
                var editor = host.All<DonorEditorView>().Single();
                var fields = host.All<FormField>().Where(f => editor.IsAncestorOf(f)).ToList();
                FormField Field(string label) => fields.Single(f => f.Label.StartsWith(label, StringComparison.Ordinal));
                var name = Field("Full name");
                var dob = Field("Date of birth");
                var gender = Field("Gender");
                var blood = Field("Blood type");
                var frequency = Field("Donates");
                var phone = Field("Phone");
                var address = Field("Address");
                var b = (Func<FrameworkElement, Rect>)(e => FeatureHarness.BoundsIn(e, editor));
                var save = host.Find<Button>("SaveButton");
                var cancel = host.Find<Button>("CancelButton");

                return (
                    Required: fields.Where(f => f.IsRequired).Select(f => f.Label).ToList(),
                    OptionalMarked: new[] { phone, address }.Any(f => f.Label.Contains("required", StringComparison.Ordinal)),
                    ColumnOneLeft: new[] { b(name).Left, b(dob).Left, b(blood).Left, b(phone).Left }.Max() - new[] { b(name).Left, b(dob).Left, b(blood).Left, b(phone).Left }.Min(),
                    ColumnTwoLeft: new[] { b(gender).Left, b(frequency).Left, b(address).Left }.Max() - new[] { b(gender).Left, b(frequency).Left, b(address).Left }.Min(),
                    SecondColumnRightOfFirst: b(gender).Left > b(dob).Right,
                    Grouped: b(dob).Top > b(name).Top && b(phone).Top > b(blood).Top,
                    SaveRightOfCancel: b(save).Left > b(cancel).Right,
                    SaveAtBottomRight: b(save).Bottom > b(phone).Bottom && b(save).Right >= b(address).Right - 1,
                    CancelEnabled: cancel.IsEnabled,
                    SaveText: save.Content?.ToString(),
                    Title: vm.Editor!.Title);
            }
        });

        Assert.Equal(5, facts.Required.Count);
        Assert.All(facts.Required, label => Assert.Contains("(required)", label, StringComparison.Ordinal)); // the word is on screen, not only a colour
        Assert.False(facts.OptionalMarked);
        Assert.True(facts.ColumnOneLeft < 1, $"left edges of column one differ by {facts.ColumnOneLeft}");
        Assert.True(facts.ColumnTwoLeft < 1, $"left edges of column two differ by {facts.ColumnTwoLeft}");
        Assert.True(facts.SecondColumnRightOfFirst);
        Assert.True(facts.Grouped);
        Assert.True(facts.SaveRightOfCancel);
        Assert.True(facts.SaveAtBottomRight);
        Assert.True(facts.CancelEnabled);
        Assert.Equal("Add donor", facts.SaveText);
        Assert.Equal("New donor", facts.Title);
    }

    [Fact]
    public void DonorForm_DobInFuture_InlineError_FocusMoves()
    {
        var facts = StaHost.Run(() =>
        {
            var (host, vm, rig) = OpenNewDonorForm();
            using (host)
            using (rig)
            {
                var form = (DonorEditorViewModel)vm.Editor!;
                form.Name = "Maria Lopez";
                form.DateOfBirth = DateOnly.FromDateTime(DateTime.Today).AddDays(2);
                form.Gender = Lifestream.Domain.Gender.Female;
                form.BloodType = Lifestream.Domain.BloodType.APos;
                form.Frequency = Lifestream.Domain.DonorFrequency.Regular;
                host.Settle(100);

                Hosted.Await(() => form.SaveCommand.ExecuteAsync());
                host.Settle(300);
                FeatureHarness.Save(host, "DonorForm_DobInFuture.png");

                var editor = host.All<DonorEditorView>().Single();
                var fields = host.All<FormField>().Where(f => editor.IsAncestorOf(f)).ToList();
                var dob = fields.Single(f => f.Label.StartsWith("Date of birth", StringComparison.Ordinal));
                var name = fields.Single(f => f.Label.StartsWith("Full name", StringComparison.Ordinal));
                var errorText = host.All<TextBlock>().Where(t => t.IsVisible && t.Name == "PART_Error" && t.Text.Length > 0).Select(t => t.Text).ToList();
                return (dob.Error, dob.HasError, DobFocusRequests: dob.FocusRequestCount, NameFocusRequests: name.FocusRequestCount, VisibleErrors: errorText, Calls: rig.Api.CountOf("CreateDonorAsync"));
            }
        });

        Assert.Equal("The date of birth must be in the past.", facts.Error);
        Assert.True(facts.HasError);
        Assert.Equal(1, facts.DobFocusRequests); // focus moved to the first invalid field
        Assert.Equal(0, facts.NameFocusRequests);
        Assert.Equal(["The date of birth must be in the past."], facts.VisibleErrors); // icon-and-text message under the field
        Assert.Equal(0, facts.Calls); // nothing was sent
    }

    [Fact]
    public void DonorForm_EmptySubmit_ShowsEveryRequiredMessage_AndFocusesTheFirstField()
    {
        var facts = StaHost.Run(() =>
        {
            var (host, vm, rig) = OpenNewDonorForm();
            using (host)
            using (rig)
            {
                var form = (DonorEditorViewModel)vm.Editor!;
                Hosted.Await(() => form.SaveCommand.ExecuteAsync());
                host.Settle(300);
                FeatureHarness.Save(host, "DonorForm_Errors.png");
                var editor = host.All<DonorEditorView>().Single();
                var fields = host.All<FormField>().Where(f => editor.IsAncestorOf(f)).ToList();
                return (WithErrors: fields.Count(f => f.HasError), NameFocus: fields.Single(f => f.Label.StartsWith("Full name", StringComparison.Ordinal)).FocusRequestCount, Summary: form.ErrorSummary);
            }
        });

        Assert.Equal(5, facts.WithErrors);
        Assert.Equal(1, facts.NameFocus);
        Assert.Equal("5 fields need attention.", facts.Summary);
    }

    [Fact]
    public void DonorForm_ServerFieldError_AppearsUnderItsField_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            var (host, vm, rig) = OpenNewDonorForm();
            using (host)
            using (rig)
            {
                var form = (DonorEditorViewModel)vm.Editor!;
                form.Name = "Maria Lopez";
                form.DateOfBirth = new DateOnly(1990, 1, 1);
                form.Gender = Lifestream.Domain.Gender.Female;
                form.BloodType = Lifestream.Domain.BloodType.APos;
                form.Frequency = Lifestream.Domain.DonorFrequency.Regular;
                rig.Api.On("CreateDonorAsync", ApiResult<DonorDetail>.Fail(new ApiProblem(400, ProblemCodes.Validation, "Invalid", "c", "{\"errors\":{\"phone\":[\"is already used by another donor\"]}}")));

                Hosted.Await(() => form.SaveCommand.ExecuteAsync());
                host.Settle(300);

                var editor = host.All<DonorEditorView>().Single();
                var phone = host.All<FormField>().Where(f => editor.IsAncestorOf(f)).Single(f => f.Label.StartsWith("Phone", StringComparison.Ordinal));
                return (phone.Error, phone.HasError);
            }
        });

        Assert.Equal("is already used by another donor", facts.Error);
        Assert.True(facts.HasError);
    }
}
