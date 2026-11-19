using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;
using Lifestream.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Client.Tests;

public sealed class ShellTests
{
    private sealed class CountingViewModel : ViewModelBase, INavigationParameterReceiver, IHasUnsavedChanges
    {
        public static int Created;
        public static int Loads;

        public CountingViewModel() => Interlocked.Increment(ref Created);

        public object? Parameter { get; private set; }

        public bool Disposed { get; private set; }

        public bool HasUnsavedChanges { get; set; }

        public void ReceiveParameter(object parameter) => Parameter = parameter;

        protected override Task FetchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Loads);
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static (Rig Rig, ShellViewModel Shell) CreateShell(Role role = Role.Staff, bool demo = false, Version? latest = null)
    {
        var rig = new Rig(latestVersion: latest);
        rig.SignIn(role, demo);
        return (rig, rig.Get<ShellViewModel>());
    }

    [Fact]
    public void Shell_HidesStaffForNonAdmin()
    {
        var (staffRig, staffShell) = CreateShell(Role.Staff);
        var (adminRig, adminShell) = CreateShell(Role.Admin);
        var (demoRig, demoShell) = CreateShell(Role.Staff, demo: true);
        using (staffRig)
        using (adminRig)
        using (demoRig)
        {
            Assert.DoesNotContain(staffShell.Items, i => i.Title == "Staff");
            Assert.DoesNotContain(staffShell.Groups, g => g.Title == "Administration");
            Assert.Contains(adminShell.Items, i => i.Title == "Staff");
            Assert.Contains(demoShell.Items, i => i.Title == "Staff"); // the demo account can view the staff list
        }
    }

    [Fact]
    public void Shell_Sidebar_HasAtMostEightGroupedDestinations_AndLabelsEqualPageTitles()
    {
        var (rig, shell) = CreateShell(Role.Admin);
        using (rig)
        {
            Assert.InRange(shell.Items.Count(), 1, 8);
            Assert.InRange(shell.Groups.Count, 2, 5);
            Assert.Equal(["", "Donations", "Transfusions", "Inventory", "Administration"], shell.Groups.Select(g => g.Title)); // direction A: Dashboard sits on its own, without a heading
            foreach (var destination in NavigationCatalog.Default)
            {
                var viewModel = (ViewModelBase)rig.Provider.GetRequiredService(destination.ViewModelType);
                Assert.Equal(destination.Title, viewModel.PageTitle); // menu label == page title
            }
        }
    }

    [Fact]
    public async Task Shell_WindowTitle_IsLifestream_ThenUpdatesOnEveryNavigation()
    {
        var (rig, shell) = CreateShell(Role.Admin);
        using (rig)
        {
            Assert.Equal("Lifestream", shell.WindowTitle);
            var titles = new List<string>();
            shell.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShellViewModel.WindowTitle))
                {
                    titles.Add(shell.WindowTitle);
                }
            };

            await shell.StartAsync();
            await shell.NavigateCommand.ExecuteAsync(shell.Items.First(i => i.Title == "Donors"));
            await shell.NavigateCommand.ExecuteAsync(shell.Items.First(i => i.Title == "Stock"));

            Assert.Equal(["Lifestream, Dashboard", "Lifestream, Donors", "Lifestream, Stock"], titles);
            Assert.Equal("Lifestream, Stock", shell.WindowTitle);
            Assert.True(shell.Items.First(i => i.Title == "Stock").IsSelected);
            Assert.False(shell.Items.First(i => i.Title == "Donors").IsSelected);
        }

        using var loginRig = new Rig();
        Assert.Equal("Lifestream", loginRig.Get<LoginViewModel>().PageTitle);
    }

    [Fact]
    public async Task Shell_OpeningADestinationTheRoleCannotUse_ShowsAccessDenied_WithBackToDashboard_AndNeverSignsOut()
    {
        var (rig, shell) = CreateShell(Role.Staff);
        using (rig)
        {
            var staff = NavigationCatalog.Default.First(d => d.Title == "Staff");

            await shell.OpenAsync(staff);

            var denied = Assert.IsType<AccessDeniedViewModel>(shell.CurrentPage);
            Assert.Equal("Back to Dashboard", denied.PrimaryActionText);
            Assert.NotNull(denied.BackToDashboardCommand);
            Assert.True(rig.Session.IsAuthenticated);

            await denied.BackToDashboardCommand!.ExecuteAsync();
            Assert.Equal("Dashboard", shell.CurrentPage!.PageTitle);
            Assert.True(rig.Session.IsAuthenticated);
            Assert.DoesNotContain("CloseMain", rig.Windows.Events);
        }
    }

    [Fact]
    public void Shell_AccountMenu_HasProfileChangePasswordAndSignOut_WithNameAndInitials()
    {
        var (rig, shell) = CreateShell(Role.Staff);
        using (rig)
        {
            Assert.NotNull(shell.ProfileCommand);
            Assert.NotNull(shell.ChangePasswordCommand);
            Assert.NotNull(shell.SignOutCommand);
            Assert.Equal("Test User", shell.DisplayName);
            Assert.Equal("TU", shell.Initials);
        }
    }

    [Fact]
    public async Task Shell_OnDemoResetHeader_ShowsToastAndRefreshesCurrentScreen()
    {
        var (rig, shell) = CreateShell(Role.Admin, demo: true);
        using (rig)
        {
            await shell.StartAsync();
            var toasts = rig.Get<IToastService>();
            var current = shell.CurrentPage;

            rig.Get<ServerStatus>().NotifyDemoReset();
            await Task.Delay(20);

            var toast = Assert.Single(toasts.Toasts);
            Assert.Contains("Demo data was reset", toast.Message);
            Assert.Same(current, shell.CurrentPage); // refreshed in place, not recreated
        }
    }

    [Fact]
    public async Task Shell_OnDemoReset_ReloadsTheCurrentScreen()
    {
        var (rig, shell) = CreateShell(Role.Admin);
        using (rig)
        {
            CountingViewModel.Loads = 0;
            await rig.Get<INavigationService>().NavigateTo<CountingViewModel>();
            Assert.Equal(1, CountingViewModel.Loads);

            rig.Get<ServerStatus>().NotifyDemoReset();
            await Task.Delay(50);

            Assert.Equal(2, CountingViewModel.Loads);
            Assert.NotNull(shell);
        }
    }

    [Fact]
    public async Task Shell_UpdateAvailable_ShowsUpdateReadyPrompt()
    {
        var (rig, shell) = CreateShell(Role.Staff, latest: new Version(2, 1, 0));
        using (rig)
        {
            Assert.Null(shell.UpdatePrompt);

            await shell.StartAsync();
            await Task.Delay(50);

            Assert.Contains("2.1.0", shell.UpdatePrompt);
            await shell.ApplyUpdateCommand.ExecuteAsync();
            Assert.Equal(1, rig.Updates.Applied);
        }
    }

    [Fact]
    public async Task Shell_NoNewerVersion_NoPrompt_AndFeedFailureIsSilent()
    {
        var (rig, shell) = CreateShell(Role.Staff, latest: new Version(2, 0, 0));
        using (rig)
        {
            await shell.StartAsync();
            await Task.Delay(50);
            Assert.Null(shell.UpdatePrompt);

            rig.Updates.Throw = true;
            await rig.Get<Lifestream.Client.Updates.UpdateService>().CheckAsync();
            Assert.Null(shell.UpdatePrompt);
        }
    }

    [Fact]
    public async Task Navigation_CreatesOneViewModelPerVisit_AndOneLoadCall()
    {
        using var rig = new Rig();
        rig.SignIn();
        var navigation = rig.Get<INavigationService>();
        CountingViewModel.Created = 0;
        CountingViewModel.Loads = 0;
        var notifications = 0;
        navigation.CurrentChanged += (_, _) => notifications++;

        await navigation.NavigateTo<CountingViewModel>();
        var first = (CountingViewModel)navigation.Current!;
        await navigation.NavigateTo<CountingViewModel>("param");
        var second = (CountingViewModel)navigation.Current!;

        Assert.Equal(2, CountingViewModel.Created);
        Assert.Equal(2, CountingViewModel.Loads);
        Assert.Equal(2, notifications); // the DataContext is replaced exactly once per visit
        Assert.NotSame(first, second);
        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        Assert.Equal("param", second.Parameter);
    }

    [Fact]
    public async Task Shell_OnDemoReset_WithUnsavedInput_KeepsTheInputAndOffersReload()
    {
        var asked = new List<string>();
        var answer = false;
        var (rig, shell) = CreateShell(Role.Admin);
        using (rig)
        {
            rig.Get<DialogService>().Presenter = new StubPresenter((title, _) =>
            {
                asked.Add(title);
                return answer;
            });
            CountingViewModel.Loads = 0;
            var navigation = rig.Get<INavigationService>();
            await navigation.NavigateTo<CountingViewModel>();
            var form = (CountingViewModel)navigation.Current!;
            form.HasUnsavedChanges = true;
            var toasts = rig.Get<IToastService>();

            rig.Get<ServerStatus>().NotifyDemoReset();
            await Task.Delay(50);

            Assert.Equal(1, CountingViewModel.Loads); // the screen was NOT reloaded under the typed input
            Assert.Same(form, navigation.Current);
            var toast = Assert.Single(toasts.Toasts);
            Assert.Contains("Demo data was reset", toast.Message);
            Assert.Equal("Reload", toast.ActionLabel);
            Assert.NotNull(shell);

            await toast.ActionCommand.ExecuteAsync(); // the user presses Reload, is asked, and stays
            Assert.Equal(1, CountingViewModel.Loads);
            Assert.Equal(["Discard changes?"], asked);

            answer = true;
            await toast.ActionCommand.ExecuteAsync(); // asked again, discards: now it reloads
            Assert.Equal(2, CountingViewModel.Loads);
        }
    }

    [Fact]
    public async Task Navigation_LeavingADirtyForm_AsksDiscardChanges_AndStaysWhenRefused()
    {
        var asked = new List<(string Title, string Verb)>();
        var answer = false;
        var presenter = new StubPresenter((title, verb) =>
        {
            asked.Add((title, verb));
            return answer;
        });
        using var rig = new Rig();
        rig.Get<DialogService>().Presenter = presenter;
        var navigation = rig.Get<INavigationService>();
        await navigation.NavigateTo<CountingViewModel>();
        var form = (CountingViewModel)navigation.Current!;
        form.HasUnsavedChanges = true;

        var stayed = await navigation.NavigateTo<CountingViewModel>();

        Assert.False(stayed);
        Assert.Same(form, navigation.Current);
        Assert.Equal([("Discard changes?", "Discard changes")], asked);

        answer = true;
        Assert.True(await navigation.NavigateTo<CountingViewModel>());
        Assert.NotSame(form, navigation.Current);
    }

    private sealed class StubPresenter(Func<string, string, bool> confirm) : IDialogPresenter
    {
        public Task<bool> ConfirmAsync(string title, string body, string dangerVerb) => Task.FromResult(confirm(title, dangerVerb));

        public Task ShowErrorAsync(string title, string message, string? details) => Task.CompletedTask;
    }

    [Fact]
    public async Task Logout_RunsOnce_ClosesOldWindow_DisposesPreviousMainViewModel_ClearsSession()
    {
        using var rig = new Rig();
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        var flow = rig.Get<IAppFlow>();
        flow.Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        var shell = rig.Windows.Main!;
        rig.Windows.Events.Clear();
        var nav = rig.Get<INavigationService>();
        Assert.NotNull(nav.Current);

        await Task.WhenAll(flow.SignOutAsync(), flow.SignOutAsync(), flow.SignOutAsync());

        Assert.Equal(["CloseMain", "ShowLogin"], rig.Windows.Events); // exactly once
        Assert.Equal(1, rig.Api.CountOf("LogoutAsync"));
        Assert.False(rig.Session.IsAuthenticated);
        Assert.Null(rig.Session.User);
        Assert.Null(rig.Session.AccessToken);
        Assert.Null(rig.Session.RefreshToken);
        Assert.Null(nav.Current); // the previous main view model was disposed (navigation reset)
        Assert.NotSame(shell, rig.Windows.Login);
        Assert.Equal(string.Empty, rig.Windows.Login!.Username);
    }

    [Fact]
    public async Task Logout_ThenSignOutAgain_IsAllowedAfterTheNextSignIn()
    {
        using var rig = new Rig();
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        var flow = rig.Get<IAppFlow>();
        flow.Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        await flow.SignOutAsync();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        await flow.SignOutAsync();

        Assert.Equal(2, rig.Api.CountOf("LogoutAsync"));
    }

    [Fact]
    public void Session_IsTheOnlySourceOfIdentity_NotThreadPrincipal()
    {
        var session = new SessionService();
        Assert.False(session.IsAuthenticated);
        Assert.Null(session.User);
        Assert.False(session.IsInRole(Role.Admin));

        session.Establish(new TokenResponse("a", "r", DateTime.UtcNow, FakeApiClient.User(Role.Admin)));
        Assert.True(session.IsInRole(Role.Admin));

        foreach (var root in new[] { "src/Lifestream.Client", "src/Lifestream.Desktop" })
        {
            foreach (var file in Directory.EnumerateFiles(RepoPaths.Find(root), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain("CurrentPrincipal", File.ReadAllText(file), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task ListViewStateStore_KeepsSearchSortAndScroll_ForTheSession_ClearedOnLogout()
    {
        using var rig = new Rig();
        var store = rig.Get<IListViewStateStore>();
        var state = store.GetState("donors");
        state.Search = "ayşe";
        state.SortColumn = "name";
        state.SortDescending = true;
        state.ScrollOffset = 240;
        state.Filters["bloodType"] = "ONeg";

        Assert.Same(state, store.GetState("donors"));
        Assert.Equal("-name", store.GetState("donors").SortExpression);
        Assert.NotSame(state, store.GetState("patients"));

        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        var flow = rig.Get<IAppFlow>();
        flow.Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        await flow.SignOutAsync();

        Assert.Null(store.GetState("donors").Search);
    }
}
