using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Features.Stock;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Client.Tests.Features;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

/// <summary>
/// The defects the owner found in the released 2.1.0 app, at the level of the view models: the bell that only jumped to the dashboard, the
/// change-password dialog that reloaded the profile behind it and left it reporting a server that was "taking too long", the demo account being offered
/// a password change the server refuses, and the icon rail that dropped the sidebar's counts.
/// </summary>
public sealed class RealAppDefectTests
{
    /// <summary>A password dialog that stays open: while it is open the clock moves on past the load's 10 s limit, as it does behind a real modal dialog.</summary>
    private sealed class OpenDialog(Action whileOpen) : IChangePasswordPrompt
    {
        public int Opened { get; private set; }

        public Task<bool> PromptAsync(bool required = false)
        {
            Opened++;
            whileOpen();
            return Task.FromResult(false);
        }
    }

    /// <summary>A screen whose answer takes long to show (a modal dialog, a slow first render): eleven seconds pass inside Apply.</summary>
    private sealed class SlowToShowViewModel(ManualTimeProvider clock) : ViewModelBase
    {
        public List<LoadState> States { get; } = [];

        protected override void Apply() => clock.Advance(TimeSpan.FromSeconds(11));

        protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName == nameof(State))
            {
                States.Add(State);
            }
        }
    }

    // ---- 2. Change password ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChangePassword_FromTheAccountMenu_OpensOnlyOnceTheProfileIsLoaded_AndTheProfileNeverTimesOutBehindTheDialog()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile()));
        ProfileViewModel? profile = null;
        LoadState? stateWhenTheDialogOpened = null;
        var dialog = new OpenDialog(() =>
        {
            stateWhenTheDialogOpened = profile!.State;
            rig.Clock.Advance(TimeSpan.FromSeconds(11));
        });
        profile = rig.Profile(prompt: dialog);
        profile.ReceiveParameter(ProfileViewModel.ChangePasswordParameter);

        await profile.LoadAsync();

        Assert.Equal(1, dialog.Opened);
        Assert.Equal(LoadState.Ready, stateWhenTheDialogOpened); // released 2.1.0: Loading, because the dialog was opened from inside the load
        Assert.Equal(LoadState.Ready, profile.State);
        Assert.Null(profile.ErrorMessage); // released 2.1.0: "The server is taking too long to answer. Check your connection, then retry."
    }

    [Fact]
    public async Task Load_OnceTheServerHasAnswered_NeverReportsATimeout_HoweverLongTheAnswerTakesToShow()
    {
        var clock = new ManualTimeProvider();
        using var screen = new SlowToShowViewModel(clock);
        screen.Attach(null, clock);

        await screen.LoadAsync();

        Assert.DoesNotContain(LoadState.Error, screen.States);
        Assert.Equal(LoadState.Ready, screen.State);
        Assert.Null(screen.ErrorMessage);
    }

    [Fact]
    public async Task ChangePassword_ChosenOnTheProfileScreen_OpensTheDialogOverIt_WithoutLoadingTheProfileAgain()
    {
        var prompt = new FakePasswordPrompt();
        using var rig = new Rig(passwordPrompt: prompt);
        rig.SignIn(Role.Admin);
        rig.Api.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile()));
        using var shell = rig.Get<ShellViewModel>();
        await shell.ProfileCommand.ExecuteAsync();
        var page = Assert.IsType<ProfileViewModel>(shell.Navigation.Current);
        page.DisplayName = "Typed and not saved yet";
        var loads = rig.Api.CountOf("GetProfileAsync");

        await shell.ChangePasswordCommand.ExecuteAsync();

        Assert.Equal([false], prompt.Prompts);
        Assert.Same(page, shell.Navigation.Current); // released 2.1.0: a new profile screen, loaded again, and what was typed was gone
        Assert.Equal(loads, rig.Api.CountOf("GetProfileAsync"));
        Assert.Equal("Typed and not saved yet", page.DisplayName);
    }

    [Fact]
    public async Task ChangePassword_ChosenOnAnotherScreen_OpensTheProfile_ThenTheDialogOnce()
    {
        var prompt = new FakePasswordPrompt();
        using var rig = new Rig(passwordPrompt: prompt);
        rig.SignIn(Role.Staff);
        rig.Api.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile()));
        using var shell = rig.Get<ShellViewModel>();

        await shell.ChangePasswordCommand.ExecuteAsync();

        var page = Assert.IsType<ProfileViewModel>(shell.Navigation.Current);
        Assert.Equal(LoadState.Ready, page.State);
        Assert.Equal([false], prompt.Prompts);
        Assert.Equal(1, rig.Api.CountOf("GetProfileAsync"));
    }

    [Fact]
    public async Task DemoAccount_IsNotOfferedAPasswordChange_AndTheProfileSaysWhy()
    {
        var prompt = new FakePasswordPrompt();
        using var rig = new Rig(passwordPrompt: prompt);
        rig.SignIn(Role.Staff, demo: true);
        rig.Api.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile()));
        using var shell = rig.Get<ShellViewModel>();

        Assert.False(shell.CanChangePassword);
        shell.Palette.Open();
        Assert.DoesNotContain(shell.Palette.Items, i => i.Title == "Change password");

        // Asked anyway, the demo account gets the profile and no dialog (the server would refuse the change with 403)...
        await shell.ChangePasswordCommand.ExecuteAsync();
        Assert.IsType<ProfileViewModel>(shell.Navigation.Current);
        Assert.Empty(prompt.Prompts);

        // ...also when the profile is told to open with the dialog.
        await shell.Navigation.NavigateTo<ProfileViewModel>(ProfileViewModel.ChangePasswordParameter);
        var page = Assert.IsType<ProfileViewModel>(shell.Navigation.Current);
        Assert.Equal(LoadState.Ready, page.State);
        Assert.False(page.CanChangePassword);
        Assert.False(page.ChangePasswordCommand.CanExecute(null));
        Assert.Equal("The demo account is shared by everyone who tries Lifestream, so its password can't be changed.", page.PasswordSectionText);
        Assert.Empty(prompt.Prompts);
    }

    [Fact]
    public async Task StaffAccount_IsOfferedThePasswordChange_InTheMenuThePaletteAndOnTheProfile()
    {
        using var rig = new Rig(passwordPrompt: new FakePasswordPrompt());
        rig.SignIn(Role.Staff);
        rig.Api.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile()));
        using var shell = rig.Get<ShellViewModel>();

        Assert.True(shell.CanChangePassword);
        shell.Palette.Open();
        Assert.Contains(shell.Palette.Items, i => i.Title == "Change password");
        await shell.ProfileCommand.ExecuteAsync();
        var page = Assert.IsType<ProfileViewModel>(shell.Navigation.Current);
        Assert.True(page.CanChangePassword);
        Assert.True(page.ChangePasswordCommand.CanExecute(null));
        Assert.Equal("Change the password you sign in with. You'll need the current one.", page.PasswordSectionText);
    }

    // ---- 1. The bell: notifications ---------------------------------------------------------------------------------------------

    private static RequestListItem Request(int id, string patient, BloodType type, int units, DateOnly requiredBy) =>
        new(id, id, patient, type, units, new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), requiredBy, RequestStatus.Pending);

    /// <summary>The three lists "Needs attention" is built from: the sample stock (three groups below their minimum, bags expiring), one request the stock cannot cover and one due today that it can.</summary>
    private static (IReadOnlyList<StockItem> Stock, IReadOnlyList<RequestListItem> Requests) Wire(Rig rig, bool stockFails = false, bool nothingToDo = false)
    {
        var today = DateOnly.FromDateTime(TimeProvider.System.GetLocalNow().DateTime);
        IReadOnlyList<StockItem> stock = nothingToDo ? [.. Samples.Stock().Select(s => s with { Available = 40, ExpiringSoon = 0, BelowThreshold = false })] : Samples.Stock();
        IReadOnlyList<RequestListItem> requests = nothingToDo ? [] : [Request(11, "Nadia Jovanovic", BloodType.ABNeg, 30, today.AddDays(2)), Request(12, "Omar Haddad", BloodType.OPos, 4, today)];
        rig.Api.On("GetStockAsync", stockFails ? FakeApiClient.Fail<IReadOnlyList<StockItem>>(503, "server_error") : ApiResult<IReadOnlyList<StockItem>>.Ok(stock));
        rig.Api.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([.. requests], requests.Count, 1, 50)));
        rig.Api.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(new([], 0, 1, 50)));
        rig.Api.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(Samples.Dashboard()));
        return (stock, requests);
    }

    [Fact]
    public async Task Bell_OpensTheNotifications_WithTheDashboardsNeedsAttentionItems_AndDoesNotLeaveTheScreen()
    {
        using var rig = new Rig();
        rig.SignIn(Role.Staff);
        var (stock, requests) = Wire(rig);
        using var shell = rig.Get<ShellViewModel>();
        await shell.OpenByTitleAsync("Donors");
        var page = shell.Navigation.Current;

        await shell.Notifications.ToggleCommand.ExecuteAsync();
        await shell.Notifications.PendingLoad;

        Assert.True(shell.Notifications.IsOpen);
        Assert.Same(page, shell.Navigation.Current); // released 2.1.0: the bell opened the dashboard
        var expected = AttentionBuilder.Build(stock, requests, [], TimeProvider.System);
        Assert.Equal(4, expected.Count); // a request the stock cannot cover, bags about to expire, groups below minimum, a request due today
        Assert.Equal(expected, shell.Notifications.Items);
        Assert.Equal(["Open request", "Review bags", "Open stock", "Fulfil"], shell.Notifications.Items.Select(i => i.ActionText));
        Assert.Equal("4 items", shell.Notifications.CountText);
        Assert.True(shell.HasNotifications);
        Assert.Equal("Notifications, 4 items need attention", shell.NotificationsSummary);

        await shell.Notifications.ToggleCommand.ExecuteAsync(); // the bell again closes it
        Assert.False(shell.Notifications.IsOpen);
    }

    [Fact]
    public async Task Notifications_ShowExactlyWhatTheDashboardShows_AndTheBellKnowsItAsSoonAsTheDashboardHasLoaded()
    {
        using var rig = new Rig();
        rig.SignIn(Role.Staff);
        Wire(rig);
        using var shell = rig.Get<ShellViewModel>();
        Assert.False(shell.Notifications.IsKnown);

        await shell.OpenByTitleAsync("Dashboard");

        var dashboard = Assert.IsType<DashboardViewModel>(shell.Navigation.Current);
        Assert.NotEmpty(dashboard.Attention);
        Assert.True(shell.Notifications.IsKnown); // no second round of calls: the dashboard handed its list over
        Assert.Equal(dashboard.Attention, shell.Notifications.Items);
        Assert.True(shell.HasNotifications);

        await shell.Notifications.ToggleCommand.ExecuteAsync();
        await shell.Notifications.PendingLoad;
        Assert.Equal(dashboard.Attention, shell.Notifications.Items);
    }

    [Fact]
    public async Task Notifications_EachItemRunsItsAction_ViewAllOpensTheDashboard_AndBothCloseTheFlyout()
    {
        using var rig = new Rig();
        rig.SignIn(Role.Staff);
        Wire(rig);
        using var shell = rig.Get<ShellViewModel>();
        await shell.OpenByTitleAsync("Donors");
        shell.Notifications.Open();
        await shell.Notifications.PendingLoad;

        await shell.Notifications.RunCommand.ExecuteAsync(shell.Notifications.Items.Single(i => i.ActionText == "Open stock"));

        Assert.False(shell.Notifications.IsOpen);
        Assert.IsType<StockViewModel>(shell.Navigation.Current);

        shell.Notifications.Open();
        await shell.Notifications.PendingLoad;
        await shell.Notifications.ViewAllCommand.ExecuteAsync();

        Assert.False(shell.Notifications.IsOpen);
        Assert.IsType<DashboardViewModel>(shell.Navigation.Current);
    }

    [Fact]
    public async Task Notifications_WithNothingToDo_SayNothingNeedsAttention_AndTheBellHasNoDot()
    {
        using var rig = new Rig();
        rig.SignIn(Role.Staff);
        Wire(rig, nothingToDo: true);
        using var shell = rig.Get<ShellViewModel>();

        shell.Notifications.Open();
        await shell.Notifications.PendingLoad;

        Assert.Equal(LoadState.Empty, shell.Notifications.State);
        Assert.Empty(shell.Notifications.Items);
        Assert.Equal("Nothing needs attention", shell.Notifications.EmptyText);
        Assert.False(shell.HasNotifications);
        Assert.Equal("Notifications, nothing needs attention", shell.NotificationsSummary);
    }

    [Fact]
    public async Task Notifications_WhenTheServerDoesNotAnswer_ShowTheError_NeverNothingNeedsAttention()
    {
        using var rig = new Rig();
        rig.SignIn(Role.Staff);
        Wire(rig, stockFails: true);
        using var shell = rig.Get<ShellViewModel>();

        shell.Notifications.Open();
        await shell.Notifications.PendingLoad;

        Assert.Equal(LoadState.Error, shell.Notifications.State);
        Assert.False(string.IsNullOrEmpty(shell.Notifications.ErrorMessage));

        // The dashboard shows what it could read, but a list that rests on a failed call is not handed to the bell.
        shell.Notifications.Close();
        await shell.OpenByTitleAsync("Dashboard");
        var dashboard = Assert.IsType<DashboardViewModel>(shell.Navigation.Current);
        Assert.False(dashboard.AttentionIsComplete);
        Assert.NotEqual(LoadState.Empty, shell.Notifications.State);
    }

    [Fact]
    public async Task Notifications_CloseWhenThePersonGoesToAnotherScreen()
    {
        using var rig = new Rig();
        rig.SignIn(Role.Staff);
        Wire(rig);
        using var shell = rig.Get<ShellViewModel>();
        shell.Notifications.Open();
        await shell.Notifications.PendingLoad;

        await shell.OpenByTitleAsync("Donors");

        Assert.False(shell.Notifications.IsOpen);
    }

    // ---- 3. The icon rail ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("7", "7")]
    [InlineData("40", "40")]
    [InlineData("128", "99+")]
    public void Rail_KeepsTheSidebarsCount_AsAShortBadge(string? count, string? onTheRail)
    {
        var item = new NavItem(NavigationCatalog.Default[2]) { Badge = count };

        Assert.Equal(onTheRail, item.RailBadge);
    }
}
