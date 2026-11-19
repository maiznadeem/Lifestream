using System.Reflection;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Navigation;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Desktop.Shell;
using Lifestream.Desktop.Tests.Features;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// SHELL-07, the last gaps of the shell-level Undo (real shell view, real view models and toast service, fake API, off-screen): a cancelled or failed Undo can be
/// retried, a waiting Undo stops at sign-out, and a waiting Undo is never hidden behind a modal. Ported from the verifier's probes (V12eProbes, V12eProbes2).
/// </summary>
[Collection("Wpf")]
public sealed class UndoRetryTests
{
    private sealed class Yes : IDialogPresenter
    {
        public Task<bool> ConfirmAsync(string title, string body, string dangerVerb) => Task.FromResult(true);

        public Task ShowErrorAsync(string title, string message, string? details) => Task.CompletedTask;
    }

    private sealed class CountingForms : IFormDialogPresenter
    {
        public List<string> Opened { get; } = [];

        public Task ShowAsync(DialogViewModel viewModel)
        {
            Opened.Add(viewModel.GetType().Name);
            return new TaskCompletionSource().Task; // a modal that stays open
        }
    }

    private static void Click(Button button) => ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button)!).Invoke();

    private static Button? Visible(Hosted host, string label) => host.All<Button>().FirstOrDefault(b => b.IsVisible && b.Content as string == label);

    private static void SelectFirst(ViewModelBase vm) =>
        Hosted.Await(() =>
        {
            var items = (System.Collections.IList)vm.GetType().GetProperty("Items")!.GetValue(vm)!;
            vm.GetType().GetProperty("SelectedItem")!.SetValue(vm, items[0]);
            return (Task)vm.GetType().GetProperty("PendingDetail", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        });

    /// <summary>Every API method waits for ever (until its token is cancelled); returns the tokens so a test can see which request was cancelled.</summary>
    private static List<(string Method, CancellationToken Token)> Silence(FakeApiClient fake)
    {
        var waiting = new List<(string, CancellationToken)>();
        foreach (var method in typeof(ILifestreamApiClient).GetMethods())
        {
            var name = method.Name;
            fake.Handlers[name] = async ct =>
            {
                lock (waiting)
                {
                    waiting.Add((name, ct));
                }

                await Task.Delay(Timeout.Infinite, ct);
                return ApiResult.Success;
            };
        }

        return waiting;
    }

    private static void DeleteFirstDonor(Rig rig, Hosted host, out DonorsViewModel donors, out ToastService toasts)
    {
        var nav = rig.Get<INavigationService>();
        Hosted.Await(() => nav.NavigateTo<DonorsViewModel>());
        host.Settle(200);
        donors = (DonorsViewModel)nav.Current!;
        SelectFirst(donors);
        host.Settle(100);
        toasts = rig.Get<ToastService>();
        var vm = donors;
        Hosted.Await(() =>
        {
            vm.DeleteCommand.Execute(null);
            return Task.CompletedTask;
        });
        var t = toasts;
        Hosted.PumpUntil(() => t.Toasts.Any(x => x.HasAction), 5000);
        host.Settle(100);
    }

    /// <param name="mode">cancel: the user cancels the waiting Undo; waking: the restore ends with 503 database_waking; unreachable: it ends with client_unreachable (what the client returns after its 120 s budget).</param>
    [Theory]
    [InlineData("cancel")]
    [InlineData("waking")]
    [InlineData("unreachable")]
    public void Undo_CancelledOrFailed_ShowsStillDeletedWithRetryUndo_AndRetryRunsTheRestoreAgainWithTheWakingNoticeAndCancel_FakeApi(string mode)
    {
        StaHost.Run(() =>
        {
            var rig = Part2Harness.NewRig();
            rig.Get<DialogService>().Presenter = new Yes();
            var shell = rig.Get<ShellViewModel>();
            using var host = Hosted.Create(new ShellView { DataContext = shell }, 1366, 736);
            DeleteFirstDonor(rig, host, out _, out var toasts);

            var restores = new List<CancellationToken>();
            rig.Api.On<DonorDetail>("RestoreDonorAsync", async ct =>
            {
                restores.Add(ct);
                if (mode == "cancel")
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return null!;
                }

                await Task.Delay(300, ct);
                return mode == "waking" ? FakeApiClient.Fail<DonorDetail>(503, ProblemCodes.DatabaseWaking) : FakeApiClient.Fail<DonorDetail>(0, "client_unreachable");
            });
            Click(Visible(host, "Undo")!);
            host.Settle(150);
            if (mode == "cancel")
            {
                shell.CancelToastActionCommand.Execute(null);
            }

            Hosted.RunDispatcherFor(1000);
            host.Settle(200);

            var info = string.Join(" | ", toasts.Toasts.Select(t => $"{t.Kind}:'{t.Message}' action={(t.HasAction ? t.ActionLabel : "none")}"));
            var retry = toasts.Toasts.SingleOrDefault(t => t.ActionLabel == "Retry undo");
            Assert.True(retry is not null, $"no 'Retry undo' toast after a {mode} Undo; toasts=[{info}]");
            Assert.Contains("still deleted", retry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(Visible(host, "Retry undo"));

            // Retry runs the restore again, on the same shell-level runner: waking notice after 3 s, with a Cancel that cancels this very request.
            rig.Api.On<DonorDetail>("RestoreDonorAsync", async ct =>
            {
                restores.Add(ct);
                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            });
            Click(Visible(host, "Retry undo")!);
            host.Settle(150);
            Assert.Equal(2, restores.Count);
            rig.Get<ServerStatus>().Enter();
            Hosted.RunDispatcherFor(3600);
            var notice = host.Find<Lifestream.Desktop.Controls.WakingNotice>("ToastActionWakingNotice");
            var cancel = Hosted.Descendants(notice).OfType<Button>().FirstOrDefault(b => b.IsVisible && b.Content as string == "Cancel");
            Assert.True(cancel is not null, "the retried Undo shows no 'Waking up the server' notice with Cancel after 3 s");
            Click(cancel);
            Hosted.RunDispatcherFor(300);
            Assert.True(restores[1].IsCancellationRequested, "Cancel did not cancel the retried restore's request");
            return 0;
        });
    }

    [Fact]
    public void SignOut_WhileAnUndoWaits_CancelsItBeforeTheSessionIsCleared_AndLeavesNoToastBehind_FakeApi()
    {
        StaHost.Run(() =>
        {
            var rig = Part2Harness.NewRig();
            rig.Get<DialogService>().Presenter = new Yes();
            var flow = rig.Get<IAppFlow>();
            Hosted.Await(() => flow.SignedInAsync());
            var shell = rig.Windows.Main!;
            using var host = Hosted.Create(new ShellView { DataContext = shell }, 1366, 736);
            DeleteFirstDonor(rig, host, out _, out var toasts);
            var waiting = Silence(rig.Api);
            Click(Visible(host, "Undo")!);
            host.Settle(150);
            rig.Get<ServerStatus>().Enter();
            Hosted.RunDispatcherFor(3600);
            Assert.True(toasts.Actions.IsWaking, "set-up: the Undo is not waiting");

            var signedInWhenCancelled = new List<bool>();
            CancellationToken restore;
            lock (waiting)
            {
                restore = waiting.Single(w => w.Method == "RestoreDonorAsync").Token;
            }

            using var registration = restore.Register(() => signedInWhenCancelled.Add(rig.Session.User is not null));
            Hosted.Await(() => flow.SignOutAsync());
            Hosted.RunDispatcherFor(500);

            Assert.True(restore.IsCancellationRequested, "sign-out left the waiting Undo running");
            Assert.False(toasts.Actions.IsRunning);
            Assert.Equal([true], signedInWhenCancelled); // cancelled while the session still existed: before it was cleared
            Assert.Null(rig.Session.User);
            Assert.Empty(toasts.Toasts); // no 'Retry undo' (or any other) toast outlives the sign-out for the next person
            return 0;
        });
    }

    [Fact]
    public void WhileAShellUndoWaits_OpeningAModalFormIsDisabled_AndEnabledAgainAfterCancel_FakeApi()
    {
        StaHost.Run(() =>
        {
            var rig = Part2Harness.NewRig();
            var forms = new CountingForms();
            rig.Get<DialogService>().Presenter = new Yes();
            rig.Get<FormDialogService>().Presenter = forms;
            var shell = rig.Get<ShellViewModel>();
            var nav = rig.Get<INavigationService>();
            using var host = Hosted.Create(new ShellView { DataContext = shell }, 1366, 736);
            DeleteFirstDonor(rig, host, out _, out var toasts);
            Hosted.Await(() => nav.NavigateTo<AppointmentsViewModel>());
            host.Settle(250);
            var appointments = (AppointmentsViewModel)nav.Current!;
            SelectFirst(appointments);
            host.Settle(150);
            Assert.True(appointments.RecordDonationCommand.CanExecute(null) && appointments.CancelAppointmentCommand.CanExecute(null), "set-up: the pending appointment's modal actions should be enabled");

            Silence(rig.Api);
            Click(Visible(host, "Undo")!);
            host.Settle(150);
            rig.Get<ServerStatus>().Enter();
            Hosted.RunDispatcherFor(3600);
            Assert.True(toasts.Actions.IsWaking, "set-up: the Undo is not waiting");

            Assert.False(appointments.RecordDonationCommand.CanExecute(null));
            Assert.False(appointments.CancelAppointmentCommand.CanExecute(null));
            Hosted.Await(() =>
            {
                appointments.RecordDonationCommand.Execute(null);
                return Task.CompletedTask;
            });
            host.Settle(200);
            Assert.Empty(forms.Opened); // no modal form opened over the shell's waking notice

            shell.CancelToastActionCommand.Execute(null);
            Hosted.RunDispatcherFor(500);
            Assert.True(appointments.RecordDonationCommand.CanExecute(null), "the modal action stays disabled after the Undo ended");
            return 0;
        });
    }
}
