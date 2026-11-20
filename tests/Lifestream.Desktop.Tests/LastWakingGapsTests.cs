using System.Reflection;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Auth;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Tests.Features;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// SHELL-07, the last gaps of the fifth round (b-12e-shell6): the sign-in Cancel after Retry, and a modal dialog that could hide a screen's Cancel.
/// (The Undo after leaving the screen is the NavigatedAway case of WakingEveryOperationTests.) Every test here uses the fake API; the server never answers.
/// </summary>
[Collection("Wpf")]
public sealed class LastWakingGapsTests
{
    private sealed class Yes : IDialogPresenter
    {
        public int Opened { get; private set; }

        public Task<bool> ConfirmAsync(string title, string body, string dangerVerb, string keepVerb)
        {
            Opened++;
            return Task.FromResult(true);
        }

        public Task ShowErrorAsync(string title, string message, string? details) => Task.CompletedTask;
    }

    private sealed class CountingForms : IFormDialogPresenter
    {
        public int Opened { get; private set; }

        public Task ShowAsync(DialogViewModel viewModel)
        {
            Opened++;
            return new TaskCompletionSource().Task; // modal: it stays open
        }
    }

    private sealed class CountingViewer : IReceiptViewer
    {
        public int Opened { get; private set; }

        public Task ShowAsync(int receiptId, bool isDuplicate)
        {
            Opened++;
            return Task.CompletedTask;
        }
    }

    private static void Click(Button button) => ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button)!).Invoke();

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

    private static int WakingTexts(Hosted host) => host.All<TextBlock>().Count(t => t.IsVisible && (t.Text ?? string.Empty).Contains("Waking up the server", StringComparison.Ordinal));

    private static List<Button> VisibleCancels(Hosted host) => [.. host.All<Button>().Where(b => b.IsVisible && b.Content as string == "Cancel")];

    private static void SelectFirst(ViewModelBase vm) =>
        Hosted.Await(() =>
        {
            var items = (System.Collections.IList)vm.GetType().GetProperty("Items")!.GetValue(vm)!;
            vm.GetType().GetProperty("SelectedItem")!.SetValue(vm, items[0]);
            return (Task)vm.GetType().GetProperty("PendingDetail", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        });

    /// <summary>The first sign-in ends with "the server is still waking up" (Retry offered); the user presses Retry, the server never answers, and the user presses Cancel.</summary>
    [Fact]
    public void Login_RetryThenCancel_FakeApi_CancelsTheRetriedRequest_AndFreesTheScreen()
    {
        var seen = StaHost.Run(() =>
        {
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            var attempts = new List<CancellationToken>();
            rig.Api.On<TokenResponse>("LoginAsync", async ct =>
            {
                attempts.Add(ct);
                if (attempts.Count == 1)
                {
                    return FakeApiClient.Fail<TokenResponse>(503, ProblemCodes.DatabaseWaking);
                }

                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            });
            var vm = rig.Get<LoginViewModel>();
            using var host = Hosted.Create(new LoginView { DataContext = vm }, 520, 688);
            host.Settle(150);
            vm.Username = "u";
            vm.Password = TestSecrets.P;
            host.Settle(100);
            Click(host.Find<Button>("SignInButton"));
            Hosted.PumpUntil(() => !vm.IsBusy, 2000);
            host.Settle(150);
            var retry = host.All<Button>().FirstOrDefault(b => b.IsVisible && b.Content as string == "Retry");
            Assert.NotNull(retry);
            Click(retry);
            host.Settle(100);
            rig.Get<ServerStatus>().Enter();
            host.Settle(250);
            var cancel = host.Find<Button>("CancelButton");
            var cancelVisible = cancel.IsVisible;
            Click(cancel);
            Hosted.PumpUntil(() => !vm.IsBusy, 2000);
            host.Settle(150);
            return (Attempts: attempts.Count, CancelVisible: cancelVisible, Cancelled: attempts[^1].IsCancellationRequested, vm.IsBusy, Running: vm.RetryCommand.IsRunning, SignInUsable: host.Find<Button>("SignInButton").IsEnabled);
        });

        Assert.Equal(2, seen.Attempts);
        Assert.True(seen.CancelVisible, "Cancel must be visible while the retried sign-in waits");
        Assert.True(seen.Cancelled, "Cancel did not cancel the HTTP request of the retried sign-in");
        Assert.False(seen.IsBusy, "the login screen stays busy after Cancel");
        Assert.False(seen.Running, "Retry is still running after Cancel");
        Assert.True(seen.SignInUsable, "Sign in is not usable again after Cancel");
    }

    /// <summary>The same for the demo: Try the demo gets a waking answer, Retry waits, and Cancel stops it.</summary>
    [Fact]
    public void Login_DemoRetryThenCancel_FakeApi_CancelsTheRetriedRequest()
    {
        var (cancelled, busy) = StaHost.Run(() =>
        {
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            var attempts = new List<CancellationToken>();
            rig.Api.On<TokenResponse>("DemoLoginAsync", async ct =>
            {
                attempts.Add(ct);
                if (attempts.Count == 1)
                {
                    return FakeApiClient.Fail<TokenResponse>(503, ProblemCodes.DatabaseWaking);
                }

                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            });
            var vm = rig.Get<LoginViewModel>();
            using var host = Hosted.Create(new LoginView { DataContext = vm }, 520, 688);
            host.Settle(150);
            Hosted.Await(() => vm.DemoCommand.ExecuteAsync());
            host.Settle(150);
            Hosted.Await(() =>
            {
                vm.RetryCommand.Execute(null);
                return Task.CompletedTask;
            });
            host.Settle(150);
            Assert.True(vm.IsBusy);
            vm.CancelCommand.Execute(null);
            Hosted.PumpUntil(() => !vm.IsBusy, 2000);
            return (attempts[^1].IsCancellationRequested, vm.IsBusy);
        });

        Assert.True(cancelled, "Cancel did not cancel the retried demo request");
        Assert.False(busy);
    }

    /// <summary>The verifier's secondary finding: Cancel appointment waits, and "Record donation" (a modal dialog) used to stay enabled, so its dialog could hide the screen's Cancel.</summary>
    [Fact]
    public void Appointments_WhileCancelAppointmentWaits_RecordDonationIsDisabled_UntilTheOperationEnds_FakeApi()
    {
        var seen = StaHost.Run(() =>
        {
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var rig = Part2Harness.NewRig(clock: clock);
            var forms = new CountingForms();
            var viewer = new CountingViewer();
            rig.Get<DialogService>().Presenter = new Yes();
            rig.Get<FormDialogService>().Presenter = forms;
            var vm = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<AppointmentsViewModel>(rig.Provider, new FakeNavigation(), viewer);
            vm.Attach(rig.Get<ServerStatus>(), clock);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new Lifestream.Desktop.Features.Appointments.AppointmentsView(), vm), 1366, 736);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(100);
            SelectFirst(vm);
            host.Settle(150);
            var enabledBefore = vm.RecordDonationCommand.CanExecute(null);
            var waiting = Silence(rig.Api);
            Hosted.Await(() =>
            {
                vm.CancelAppointmentCommand.Execute(null);
                return Task.CompletedTask;
            });
            host.Settle(150);
            rig.Get<ServerStatus>().Enter();
            clock.Advance(TimeSpan.FromMilliseconds(3600)); // over the 3 s waking threshold, on the screen's clock
            host.Settle(100);
            var disabledWhileWaiting = !vm.RecordDonationCommand.CanExecute(null);
            var recordButtons = host.All<Button>().Where(b => b.IsVisible && System.Windows.Automation.AutomationProperties.GetName(b) == "Record donation").ToList();
            var buttonDisabled = recordButtons.Count > 0 && recordButtons.All(b => !b.IsEnabled);
            var texts = WakingTexts(host);
            var cancels = VisibleCancels(host);
            Hosted.Await(() =>
            {
                vm.RecordDonationCommand.Execute(null); // nothing may open
                return Task.CompletedTask;
            });
            host.Settle(150);
            var opened = forms.Opened;
            foreach (var cancel in cancels)
            {
                Click(cancel);
            }

            Hosted.PumpUntil(() => !vm.Operations.IsRunning, 3000);
            host.Settle(200);
            bool cancelled;
            lock (waiting)
            {
                cancelled = waiting.Count > 0 && waiting.All(w => w.Token.IsCancellationRequested);
            }

            return (EnabledBefore: enabledBefore, DisabledWhileWaiting: disabledWhileWaiting, ButtonDisabled: buttonDisabled, Texts: texts, Cancels: cancels.Count, Opened: opened, Cancelled: cancelled, EnabledAfter: vm.RecordDonationCommand.CanExecute(null));
        });

        Assert.True(seen.EnabledBefore, "Record donation should be available before anything runs");
        Assert.True(seen.DisabledWhileWaiting, "Record donation stays enabled while Cancel appointment waits");
        Assert.True(seen.ButtonDisabled, "the Record donation button looks enabled while Cancel appointment waits");
        Assert.True(seen.Texts > 0 && seen.Cancels > 0, "the screen shows no waking text with a Cancel");
        Assert.Equal(0, seen.Opened);
        Assert.True(seen.Cancelled, "Cancel did not cancel the request");
        Assert.True(seen.EnabledAfter, "Record donation is not available again once the operation was cancelled");
    }

    /// <summary>
    /// Behavioural inventory of the modal rule: on every screen with commands, while an operation of the screen is running (waking, after 3.6 s), pressing
    /// any command that is enabled opens no modal dialog (a confirmation, a form, the receipt viewer), so the screen's Cancel can never be hidden behind one.
    /// </summary>
    [Fact]
    public void OnEveryScreen_WhileAnOperationWaits_NoCommandCanOpenAModalDialog_FakeApi()
    {
        (List<string> Found, int ModalCommandsSeen) result;
        {
            (Type Type, string Blocker)[] screens =
            [
                (typeof(DonorsViewModel), "RetryDetailCommand"),
                (typeof(PatientsViewModel), "RetryDetailCommand"),
                (typeof(StaffViewModel), "RetryDetailCommand"),
                (typeof(AppointmentsViewModel), "RetryDetailCommand"),
                (typeof(RequestsViewModel), "RetryDetailCommand"),
                (typeof(ReceiptsViewModel), "RetryDetailCommand"),
                (typeof(ProfileViewModel), "SaveCommand"),
            ];

            // Reviewed: the commands that open a modal dialog (a confirmation, a form, the receipt viewer, the password dialog), per screen. A command that opens one but is
            // not flagged (opensModal) is not found by walking, because it may call the API before its dialog; so the list is checked against the flags.
            Dictionary<Type, string[]> modal = new()
            {
                [typeof(DonorsViewModel)] = ["DeleteCommand"],
                [typeof(PatientsViewModel)] = ["DeleteCommand"],
                [typeof(StaffViewModel)] = ["DeleteCommand", nameof(StaffViewModel.ResetPasswordCommand)],
                [typeof(AppointmentsViewModel)] = ["DeleteCommand", "RecordDonationCommand", "RecordDonationForCommand", "CancelAppointmentCommand", "ViewReceiptCommand"],
                [typeof(RequestsViewModel)] = ["DeleteCommand", "FulfilCommand", "CancelRequestCommand", "ViewReceiptCommand"],
                [typeof(ReceiptsViewModel)] = ["DeleteCommand", "OpenReceiptCommand"],
                [typeof(ProfileViewModel)] = ["PickPhotoCommand", nameof(ProfileViewModel.ChangePasswordCommand)],
            };

            // Each screen on its own rig and clock.
            var perScreen = StaHost.Run(() => screens.Select(screen =>
            {
                var (type, blocker) = screen;
                var found = new List<string>();
                var modalCommandsSeen = 0;
                var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
                var rig = Part2Harness.NewRig(clock: clock);
                var dialogs = new Yes();
                var forms = new CountingForms();
                var viewer = new CountingViewer();
                rig.Get<DialogService>().Presenter = dialogs;
                rig.Get<FormDialogService>().Presenter = forms;
                object[] fakes = [new FakeNavigation(), viewer];
                var wanted = type.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
                var vm = (ViewModelBase)Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance(rig.Provider, type, [.. fakes.Where(f => wanted.Any(t => t.IsInstanceOfType(f)))]);
                vm.Attach(rig.Get<ServerStatus>(), clock);
                Hosted.Await(() => vm.LoadAsync());
                if (type != typeof(ProfileViewModel))
                {
                    SelectFirst(vm);
                }
                else
                {
                    ((ProfileViewModel)vm).DisplayName = "A typed name";
                }

                var commands = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(AsyncRelayCommand) && p.GetIndexParameters().Length == 0)
                    .Select(p => (p.Name, Command: (AsyncRelayCommand?)p.GetValue(vm))).Where(c => c.Command is not null).ToList();
                var modalNames = commands.Where(c => vm.ModalCommands.Contains(c.Command!)).Select(c => c.Name).ToList();
                modalCommandsSeen += modalNames.Count;
                var expected = modal[type].Where(n => commands.Any(c => c.Name == n)).Order().ToList();
                if (!expected.SequenceEqual(modalNames.Order()))
                {
                    found.Add($"{type.Name}: modal-opening commands are [{string.Join(", ", expected)}] but the flagged ones are [{string.Join(", ", modalNames.Order())}]");
                }

                var parameter = type.GetProperty("SelectedItem")?.GetValue(vm);

                var waiting = Silence(rig.Api);
                var start = commands.Single(c => c.Name == blocker).Command!;
                Hosted.Await(() =>
                {
                    start.Execute(parameter);
                    return Task.CompletedTask;
                });
                Hosted.RunDispatcherFor(200);
                if (waiting.Count == 0)
                {
                    found.Add($"{type.Name}: {blocker} made no API call, so nothing was waiting");
                    return (Found: found, ModalCommandsSeen: modalCommandsSeen);
                }

                rig.Get<ServerStatus>().Enter();
                clock.Advance(TimeSpan.FromMilliseconds(3600)); // over the 3 s waking threshold, on the screen's clock
                Hosted.RunDispatcherFor(150);
                if (!vm.IsOperationWaking)
                {
                    found.Add($"{type.Name}: the operation is not in the waking state after 3.6 s");
                }

                foreach (var (name, command) in commands)
                {
                    if (name is "CancelOperationCommand" or "CancelWakingCommand" or "CancelDetailCommand" or "CancelLoadCommand" || ReferenceEquals(command, start))
                    {
                        continue; // only cancel
                    }

                    if (modalNames.Contains(name) && (command!.CanExecute(parameter) || command.CanExecute(null)))
                    {
                        found.Add($"{type.Name}.{name} opens a modal dialog and is enabled while an operation waits");
                    }

                    var before = dialogs.Opened + forms.Opened + viewer.Opened;
                    foreach (var p in new[] { parameter, null })
                    {
                        if (command!.CanExecute(p))
                        {
                            var argument = p;
                            Hosted.Await(() =>
                            {
                                command.Execute(argument);
                                return Task.CompletedTask;
                            });
                            break;
                        }
                    }

                    Hosted.RunDispatcherFor(60);
                    var opened = dialogs.Opened + forms.Opened + viewer.Opened - before;
                    if (opened > 0)
                    {
                        found.Add($"{type.Name}.{name} opened {opened} modal dialog(s) while an operation waited");
                    }
                }

                vm.Operations.Cancel();
                Hosted.PumpUntil(() => !vm.Operations.IsRunning, 3000);
                vm.Dispose();
                return (Found: found, ModalCommandsSeen: modalCommandsSeen);
            }).ToArray());

            result = (Found: perScreen.SelectMany(r => r.Found).ToList(), ModalCommandsSeen: perScreen.Sum(r => r.ModalCommandsSeen));
        }

        Assert.True(result.ModalCommandsSeen >= 12, $"only {result.ModalCommandsSeen} modal-opening commands were found: the inventory is not walking the screens");
        Assert.True(result.Found.Count == 0, string.Join(Environment.NewLine, result.Found));
    }
}
