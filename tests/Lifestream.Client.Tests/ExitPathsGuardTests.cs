using System.Reflection;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;

namespace Lifestream.Client.Tests;

/// <summary>
/// SHELL-24, behavioural inventory (fourth round): every command of the shell that can close a window, exit the process or replace the current screen is
/// run with a form with unsaved changes on screen and a user who answers "stay", and must (a) ask "Discard changes?" and (b) change nothing: no update
/// applied (which exits the process), no window closed, still signed in, same screen. A command that exits without asking fails (b); a command that does
/// nothing at all is not allowed to hide in this inventory either: each must be on the reviewed list below, which says whether it leaves the screen.
/// The reflection over the commands means a new command cannot be added without being reviewed. The list of exit APIs that the commands reach is
/// inventoried in the Desktop tests (ExitApiInventoryTests). Fake API, fake windows and a fake update source: nothing really exits.
/// </summary>
[Collection("GlobalErrorHandler")]
public sealed class ExitPathsGuardTests
{
    private sealed class Presenter : IDialogPresenter
    {
        public List<string> Asked { get; } = [];

        public Task<bool> ConfirmAsync(string title, string body, string dangerVerb)
        {
            Asked.Add(title);
            return Task.FromResult(false); // the user chooses to stay
        }

        public Task ShowErrorAsync(string title, string message, string? details) => Task.CompletedTask;
    }

    /// <summary>
    /// Every command of the shell with whether it leaves the current screen (closes a window, signs out, restarts, navigates). A leaving command must ask
    /// first; a command that does not leave is listed with the reason, so a new command is reviewed here.
    /// </summary>
    private static readonly Dictionary<string, (bool Leaves, string Reason)> Reviewed = new()
    {
        [nameof(ShellViewModel.NavigateCommand)] = (true, "opens another screen from the sidebar"),
        [nameof(ShellViewModel.ProfileCommand)] = (true, "opens the profile screen"),
        [nameof(ShellViewModel.ChangePasswordCommand)] = (true, "opens the profile screen at the password section"),
        [nameof(ShellViewModel.SignOutCommand)] = (true, "closes the main window and shows the sign-in window"),
        [nameof(ShellViewModel.ApplyUpdateCommand)] = (true, "restarts the app into the new version: the process exits"),
        [nameof(ShellViewModel.BackToDashboardCommand)] = (true, "opens the dashboard"),
        [nameof(ShellViewModel.SearchCommand)] = (false, "moves the keyboard focus to the page's search box"),
        [nameof(ShellViewModel.NewRecordCommand)] = (false, "runs the page's own new-record command; a dirty form on that page has no such command, and a list page asks itself (NewWhileDirtyTests)"),
    };

    /// <summary>The commands the view model really offers (a property that is null, such as the base class's page-specific NewCommand, is not one).</summary>
    private static IEnumerable<PropertyInfo> CommandsOf(ViewModelBase viewModel) =>
        viewModel.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(AsyncRelayCommand) && !p.IsDefined(typeof(NoOperationRunnerAttribute), false) && p.GetValue(viewModel) is not null);

    private static async Task<(Rig Rig, Presenter Presenter, ShellViewModel Shell, DiscardChangesTests.DirtyFormViewModel Form)> DirtyFormOpen()
    {
        var rig = new Rig(latestVersion: new Version(9, 0, 0));
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        var presenter = new Presenter();
        rig.Get<DialogService>().Presenter = presenter;
        rig.Get<IAppFlow>().Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        var shell = rig.Windows.Main!;
        await shell.Updates.CheckAsync();
        await rig.Get<INavigationService>().NavigateTo<DiscardChangesTests.DirtyFormViewModel>();
        var form = (DiscardChangesTests.DirtyFormViewModel)rig.Get<INavigationService>().Current!;
        form.HasUnsavedChanges = true;
        presenter.Asked.Clear();
        rig.Windows.Events.Clear();
        return (rig, presenter, shell, form);
    }

    [Fact]
    public async Task EveryShellCommand_IsOnTheReviewedList_SoANewCommandCannotSkipTheGuard()
    {
        var (rig, _, shell, _) = await DirtyFormOpen();
        using (rig)
        {
            var actual = CommandsOf(shell).Select(p => p.Name).Order().ToList();

            Assert.Equal([.. Reviewed.Keys.Order()], actual);
        }
    }

    [Fact]
    public async Task RestartAndUpdate_WithUnsavedChanges_AsksDiscardChanges_AndDoesNotRestartWhenTheUserStays_FakeUpdateFeed()
    {
        var (rig, presenter, shell, form) = await DirtyFormOpen();
        using (rig)
        {
            Assert.NotNull(shell.UpdatePrompt);

            await shell.ApplyUpdateCommand.ExecuteAsync();

            Assert.Equal(["Discard changes?"], presenter.Asked);
            Assert.Equal(0, rig.Updates.Applied); // applying restarts the app at once; nothing may have been applied
            Assert.Same(form, rig.Get<INavigationService>().Current);
        }
    }

    [Fact]
    public async Task RestartAndUpdate_WithUnsavedChanges_RestartsWhenTheUserChoosesToDiscard_FakeUpdateFeed()
    {
        var (rig, presenter, shell, _) = await DirtyFormOpen();
        using (rig)
        {
            rig.Get<DialogService>().Presenter = new AgreeingPresenter(presenter);

            await shell.ApplyUpdateCommand.ExecuteAsync();

            Assert.Equal(1, rig.Updates.Applied);
        }
    }

    [Fact]
    public async Task RestartAndUpdate_WithNothingUnsaved_RestartsWithoutAsking_FakeUpdateFeed()
    {
        var (rig, presenter, shell, form) = await DirtyFormOpen();
        using (rig)
        {
            form.HasUnsavedChanges = false;

            await shell.ApplyUpdateCommand.ExecuteAsync();

            Assert.Empty(presenter.Asked);
            Assert.Equal(1, rig.Updates.Applied);
        }
    }

    private sealed class AgreeingPresenter(Presenter record) : IDialogPresenter
    {
        public Task<bool> ConfirmAsync(string title, string body, string dangerVerb)
        {
            record.Asked.Add(title);
            return Task.FromResult(true);
        }

        public Task ShowErrorAsync(string title, string message, string? details) => Task.CompletedTask;
    }

    [Fact]
    public async Task EveryShellCommand_WithAFormDirty_AsksBeforeLeaving_AndLeavesNothingBehindWhenTheUserStays()
    {
        var problems = new List<string>();
        var leaving = 0;
        string[] names;
        var (first, _, firstShell, _) = await DirtyFormOpen();
        using (first)
        {
            names = [.. CommandsOf(firstShell).Select(p => p.Name)];
        }

        foreach (var commandName in names)
        {
            var (rig, presenter, shell, form) = await DirtyFormOpen();
            using (rig)
            {
                var property = typeof(ShellViewModel).GetProperty(commandName)!;
                var command = (AsyncRelayCommand)property.GetValue(shell)!;
                // The sidebar command needs the item to open: any destination that is not the current screen.
                object? parameter = property.Name == nameof(ShellViewModel.NavigateCommand) ? shell.Groups.SelectMany(g => g.Items).First() : null;
                Assert.True(command.CanExecute(parameter), $"{property.Name} is not executable on a signed-in shell");

                await command.ExecuteAsync(parameter);

                var (leaves, _) = Reviewed[property.Name];
                var name = property.Name;
                if (rig.Updates.Applied > 0)
                {
                    problems.Add($"{name}: applied the update (the process restarts) with unsaved changes and a user who chose to stay");
                }

                if (rig.Windows.Events.Contains("CloseMain") || rig.Windows.Events.Contains("ShowLogin") || !rig.Session.IsAuthenticated)
                {
                    problems.Add($"{name}: closed the main window or signed out with unsaved changes and a user who chose to stay");
                }

                if (!ReferenceEquals(rig.Get<INavigationService>().Current, form))
                {
                    problems.Add($"{name}: replaced the screen with unsaved changes and a user who chose to stay");
                }

                if (leaves)
                {
                    leaving++;
                    if (presenter.Asked is not ["Discard changes?"])
                    {
                        problems.Add($"{name}: leaves the screen but asked [{string.Join(", ", presenter.Asked)}] instead of exactly one 'Discard changes?'");
                    }
                }
                else if (presenter.Asked.Count > 0)
                {
                    problems.Add($"{name}: is reviewed as not leaving, but it asked [{string.Join(", ", presenter.Asked)}]");
                }
            }
        }

        Assert.True(leaving >= 6, $"only {leaving} leaving commands were exercised");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
