using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Auth;

namespace Lifestream.Client.Tests;

/// <summary>SHELL-24: leaving a form with unsaved changes by signing out or closing the window asks "Discard changes?".</summary>
public sealed class DiscardChangesTests
{
    public sealed class DirtyFormViewModel : ViewModelBase, IHasUnsavedChanges
    {
        public override string PageTitle => "Form";

        public bool HasUnsavedChanges { get; set; }
    }

    private sealed class Presenter(Func<string, bool> confirm) : IDialogPresenter
    {
        public List<(string Title, string Verb)> Asked { get; } = [];

        public Task<bool> ConfirmAsync(string title, string body, string dangerVerb, string keepVerb)
        {
            Asked.Add((title, dangerVerb));
            return Task.FromResult(confirm(title));
        }

        public Task ShowErrorAsync(string title, string message, string? details) => Task.CompletedTask;
    }

    private static async Task<(Rig Rig, Presenter Presenter, IAppFlow Flow, DirtyFormViewModel Form, ShellViewModel Shell)> DirtyFormOpen(Func<string, bool> answer)
    {
        var rig = new Rig();
        rig.Api.On("DemoLoginAsync", ApiResult<TokenResponse>.Ok(FakeApiClient.Tokens(FakeApiClient.User(demo: true))));
        var presenter = new Presenter(answer);
        rig.Get<DialogService>().Presenter = presenter;
        var flow = rig.Get<IAppFlow>();
        flow.Start();
        await rig.Windows.Login!.DemoCommand.ExecuteAsync();
        var shell = rig.Windows.Main!;
        await rig.Get<INavigationService>().NavigateTo<DirtyFormViewModel>();
        var form = (DirtyFormViewModel)rig.Get<INavigationService>().Current!;
        form.HasUnsavedChanges = true;
        return (rig, presenter, flow, form, shell);
    }

    [Fact]
    public async Task SignOut_WithUnsavedChanges_AsksDiscardChanges_AndStaysSignedInWhenCancelled()
    {
        var (rig, presenter, flow, form, _) = await DirtyFormOpen(_ => false);
        using (rig)
        {
            rig.Windows.Events.Clear();

            await flow.SignOutAsync();

            Assert.Equal([("Discard changes?", "Discard changes")], presenter.Asked);
            Assert.True(rig.Session.IsAuthenticated, "Cancel must keep the user signed in");
            Assert.DoesNotContain("CloseMain", rig.Windows.Events);
            Assert.Same(form, rig.Get<INavigationService>().Current);
        }
    }

    [Fact]
    public async Task SignOut_WithUnsavedChanges_SignsOutWhenDiscardIsConfirmed()
    {
        var (rig, presenter, flow, _, _) = await DirtyFormOpen(_ => true);
        using (rig)
        {
            await flow.SignOutAsync();

            Assert.Single(presenter.Asked);
            Assert.False(rig.Session.IsAuthenticated);
            Assert.Contains("CloseMain", rig.Windows.Events);
        }
    }

    [Fact]
    public async Task SignOut_ByAnExpiredSession_DoesNotAsk_BecauseTheSessionIsAlreadyGone()
    {
        var (rig, presenter, flow, _, _) = await DirtyFormOpen(_ => false);
        using (rig)
        {
            await flow.SignOutAsync("Your session expired. Please sign in again.", force: true);

            Assert.Empty(presenter.Asked);
            Assert.False(rig.Session.IsAuthenticated);
        }
    }

    [Fact]
    public async Task ClosingTheMainWindow_WithUnsavedChanges_AsksDiscardChanges_AndHonoursCancel()
    {
        var answer = false;
        var (rig, presenter, _, form, shell) = await DirtyFormOpen(_ => answer);
        using (rig)
        {
            Assert.True(shell.HasUnsavedChanges);
            Assert.False(await shell.ConfirmCloseAsync());
            Assert.Equal([("Discard changes?", "Discard changes")], presenter.Asked);

            answer = true;
            Assert.True(await shell.ConfirmCloseAsync());

            form.HasUnsavedChanges = false;
            Assert.False(shell.HasUnsavedChanges);
            presenter.Asked.Clear();
            Assert.True(await shell.ConfirmCloseAsync());
            Assert.Empty(presenter.Asked);
        }
    }
}
