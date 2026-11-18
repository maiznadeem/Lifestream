using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Desktop.Shell;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-24: closing the real main window with unsaved changes asks "Discard changes?" (the window is never shown).</summary>
[Collection("Wpf")]
public sealed class MainWindowCloseTests
{
    public sealed class DirtyFormViewModel : ViewModelBase, IDirtyTracking
    {
        public override string PageTitle => "Form";

        public bool HasUnsavedChanges { get; set; }
    }

    private sealed class Presenter(Func<bool> confirm) : IDialogPresenter
    {
        public int Asked { get; private set; }

        public Task<bool> ConfirmAsync(string title, string body, string dangerVerb)
        {
            Asked++;
            return Task.FromResult(title == "Discard changes?" && confirm());
        }

        public Task ShowErrorAsync(string title, string message, string? details) => Task.CompletedTask;
    }

    [Fact]
    public void ClosingTheMainWindow_WithAnUnsavedForm_Prompts_StaysOpenOnCancel_AndClosesOnDiscard()
    {
        var (afterCancel, askedAfterCancel, afterDiscard, askedAfterDiscard, cleanClose) = StaHost.Run(() =>
        {
            var answer = false;
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            rig.SignIn(Lifestream.Domain.Role.Admin);
            var presenter = new Presenter(() => answer);
            rig.Get<DialogService>().Presenter = presenter;
            var shell = rig.Get<ShellViewModel>();
            Hosted.Await(() => rig.Get<INavigationService>().NavigateTo<DirtyFormViewModel>());
            ((DirtyFormViewModel)rig.Get<INavigationService>().Current!).HasUnsavedChanges = true;

            var window = new MainWindow { DataContext = shell };
            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Close();
            Hosted.PumpUntil(() => presenter.Asked > 0, 2000);
            StaHost.Settle(100);
            var stillOpen = !closed;
            var asked1 = presenter.Asked;

            answer = true;
            window.Close();
            Hosted.PumpUntil(() => closed, 2000);
            var closedAfterDiscard = closed;
            var asked2 = presenter.Asked;

            // A window with nothing unsaved closes without a question.
            var clean = new MainWindow { DataContext = shell };
            ((DirtyFormViewModel)rig.Get<INavigationService>().Current!).HasUnsavedChanges = false;
            var cleanClosed = false;
            clean.Closed += (_, _) => cleanClosed = true;
            clean.Close();
            Hosted.PumpUntil(() => cleanClosed, 2000);
            return (stillOpen, asked1, closedAfterDiscard, asked2, cleanClosed && presenter.Asked == asked2);
        });

        Assert.True(afterCancel, "Cancel must keep the main window open");
        Assert.Equal(1, askedAfterCancel);
        Assert.True(afterDiscard, "Discard must close the window");
        Assert.Equal(2, askedAfterDiscard);
        Assert.True(cleanClose, "a window with no unsaved changes closes without asking");
    }

    [Fact]
    public void TheAppClosingTheMainWindow_AfterSignOut_DoesNotAskAgain()
    {
        var (closed, asked) = StaHost.Run(() =>
        {
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            rig.SignIn(Lifestream.Domain.Role.Admin);
            var presenter = new Presenter(() => false);
            rig.Get<DialogService>().Presenter = presenter;
            var shell = rig.Get<ShellViewModel>();
            Hosted.Await(() => rig.Get<INavigationService>().NavigateTo<DirtyFormViewModel>());
            ((DirtyFormViewModel)rig.Get<INavigationService>().Current!).HasUnsavedChanges = true;
            var window = new MainWindow { DataContext = shell };
            var isClosed = false;
            window.Closed += (_, _) => isClosed = true;

            window.CloseWithoutPrompt();
            Hosted.PumpUntil(() => isClosed, 2000);
            return (isClosed, presenter.Asked);
        });

        Assert.True(closed);
        Assert.Equal(0, asked);
    }
}
