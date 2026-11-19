using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Features.Stock;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Desktop.Features.Appointments;
using Lifestream.Desktop.Features.Dashboard;
using Lifestream.Desktop.Features.Donors;
using Lifestream.Desktop.Features.Patients;
using Lifestream.Desktop.Features.Profile;
using Lifestream.Desktop.Features.Receipts;
using Lifestream.Desktop.Features.Requests;
using Lifestream.Desktop.Features.Staff;
using Lifestream.Desktop.Features.Stock;
using Lifestream.Desktop.Tests.Features;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// SHELL-07, behavioural inventory (fourth round). For every screen of the app and every command on it, this runs the command against a server that
/// never answers, renders the real view off-screen after the 3 s waking threshold, and asserts what the user would see: the "Waking up the server" text,
/// a Cancel beside it, and that pressing that Cancel cancels the HTTP request that is waiting. Nothing is read from the view model's own flags.
/// The fake API stands in for the server (a handler that never answers and records the cancellation token it was given); the status flips to waking as
/// the wake-aware handler does after 3 s. A command that makes no API call must be in the reviewed list below, with the reason.
/// </summary>
[Collection("Wpf")]
public sealed class WakingEveryOperationTests
{
    private const int Width = 1366;
    private const int Height = 736;

    private sealed record Screen(string Name, Type ViewModel, Func<ViewModelBase, UserControl> View, bool SelectFirst = true, Action<ViewModelBase>? Prepare = null, int SelectIndex = 0)
    {
        /// <summary>The parameter to run a command with, when it is not the selected row.</summary>
        public Dictionary<string, Func<ViewModelBase, object?>> Parameters { get; init; } = [];
    }

    private static readonly Screen[] Screens =
    [
        new("Donors", typeof(DonorsViewModel), vm => Part2Harness.OnCanvas(new DonorsView(), vm)),
        new("Patients", typeof(PatientsViewModel), vm => Part2Harness.OnCanvas(new PatientsView(), vm)),
        new("Appointments", typeof(AppointmentsViewModel), vm => Part2Harness.OnCanvas(new AppointmentsView(), vm)),
        new("Requests", typeof(RequestsViewModel), vm => Part2Harness.OnCanvas(new RequestsView(), vm)),
        new("Receipts", typeof(ReceiptsViewModel), vm => Part2Harness.OnCanvas(new ReceiptsView(), vm)),
        new("Staff", typeof(StaffViewModel), vm => Part2Harness.OnCanvas(new StaffView(), vm), SelectIndex: 1)
        {
            Parameters = { ["LoadPhotoCommand"] = vm => ((StaffViewModel)vm).Items[^1] }, // a row whose thumbnail was not asked for yet
        },
        new("Stock", typeof(StockViewModel), vm => Part2Harness.OnCanvas(new StockView(), vm), SelectFirst: false),
        new("Dashboard", typeof(DashboardViewModel), vm => Part2Harness.OnCanvas(new DashboardView(), vm), SelectFirst: false),
        new("Profile", typeof(ProfileViewModel), vm => Part2Harness.OnCanvas(new ProfileView(), vm), SelectFirst: false, Prepare: vm => ((ProfileViewModel)vm).DisplayName = "A typed name"),
    ];

    /// <summary>
    /// Commands that make no API call when run on a loaded screen (they open a form, navigate, or only cancel), each with the reason. The test
    /// fails when one of these does call the API (the list is stale) and when a command not listed here makes no call (it needs a review).
    /// </summary>
    private static readonly Dictionary<string, string> NoCall = new()
    {
        ["AddCommand"] = "opens the new-record editor (the editor's own operations are covered by the editor tests)",
        ["Donors.EditCommand"] = "opens the editor",
        ["Patients.EditCommand"] = "opens the editor",
        ["Requests.EditCommand"] = "opens the editor",
        ["Staff.EditCommand"] = "opens the editor",
        ["Receipts.EditCommand"] = "receipts cannot be edited (the command is disabled or opens nothing)",
        ["Stock.ClearFiltersCommand"] = "no filter is set on a fresh screen, so there is nothing to reload",
        ["EditRequestCommand"] = "opens the editor",
        ["ResetPasswordCommand"] = "opens the reset-password dialog",
        ["BookAppointmentCommand"] = "navigates to Appointments with the donor (the next screen loads itself; the navigation is a fake here)",
        ["NewRequestCommand"] = "navigates to Requests with the patient (a fake navigation here)",
        ["NewCommand"] = "the same action as AddCommand",
        ["Receipts.DeleteCommand"] = "receipts cannot be deleted",
        ["OpenDonorCommand"] = "navigates",
        ["OpenPatientCommand"] = "navigates",
        ["ViewReceiptCommand"] = "opens the receipt viewer (its own load is covered by the viewer tests)",
        ["OpenReceiptCommand"] = "opens the receipt viewer",
        ["OpenSourceCommand"] = "navigates",
        ["OpenCardCommand"] = "navigates",
        ["OpenLowStockCommand"] = "navigates",
        ["CancelDetailCommand"] = "only cancels",
        ["CancelOperationCommand"] = "only cancels",
        ["CancelLoadCommand"] = "only cancels",
        ["DiscardCommand"] = "resets the form to what was loaded",
        ["PickPhotoCommand"] = "opens the file picker",
        ["ChangePasswordCommand"] = "opens the password dialog",
    };

    /// <summary>Commands that are disabled on a freshly loaded screen with the first record selected, each with the reason; they make no call of their own.</summary>
    private static readonly Dictionary<string, string> NotRunnable = new()
    {
        ["PreviousPageCommand"] = "disabled on the first page",
        ["NextPageCommand"] = "disabled when everything fits on one page",
        ["ViewReceiptCommand"] = "enabled only for a record that has a receipt; it opens the viewer, whose load is covered by the viewer tests",
    };

    private static bool IsNoCall(string screen, string command) => NoCall.ContainsKey($"{screen}.{command}") || NoCall.ContainsKey(command);

    private sealed class SilentViewer : IReceiptViewer
    {
        public Task ShowAsync(int receiptId, bool isDuplicate) => Task.CompletedTask;
    }

    private sealed class Yes : IDialogPresenter
    {
        public Task<bool> ConfirmAsync(string title, string body, string dangerVerb) => Task.FromResult(true);

        public Task ShowErrorAsync(string title, string message, string? details) => Task.CompletedTask;
    }

    /// <summary>The server that never answers: every API method waits until its request is cancelled and records the token it was given.</summary>
    private sealed class SilentServer
    {
        public List<(string Method, CancellationToken Token)> Waiting { get; } = [];

        public void Install(FakeApiClient fake)
        {
            foreach (var method in typeof(ILifestreamApiClient).GetMethods())
            {
                var name = method.Name;
                fake.Handlers[name] = async ct =>
                {
                    lock (Waiting)
                    {
                        Waiting.Add((name, ct));
                    }

                    await Task.Delay(Timeout.Infinite, ct);
                    return ApiResult.Success;
                };
            }
        }

        public int Count
        {
            get
            {
                lock (Waiting)
                {
                    return Waiting.Count;
                }
            }
        }
    }

    private static IEnumerable<PropertyInfo> CommandsOf(Type viewModel) =>
        viewModel.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(AsyncRelayCommand) && p.GetIndexParameters().Length == 0);

    private static object? Selected(ViewModelBase vm) => vm.GetType().GetProperty("SelectedItem")?.GetValue(vm);

    /// <summary>The visible "Waking up the server" texts and the visible Cancel button that sits with one of them (within three levels of containers).</summary>
    private static (List<TextBlock> Texts, Button? Cancel) WakingSurface(Hosted host)
    {
        var texts = host.All<TextBlock>().Where(t => t.IsVisible && (t.Text ?? string.Empty).Contains("Waking up the server", StringComparison.Ordinal)).ToList();
        foreach (var text in texts)
        {
            DependencyObject? level = text;
            for (var up = 0; up < 3 && level is not null; up++)
            {
                level = VisualTreeHelper.GetParent(level);
                var cancel = level is null ? null : Hosted.Descendants(level).OfType<Button>().FirstOrDefault(b => b.IsVisible && b.Content as string == "Cancel");
                if (cancel is not null)
                {
                    return (texts, cancel);
                }
            }
        }

        return (texts, null);
    }

    /// <summary>The screen from the client's DI, with navigation and the receipt viewer faked so no other screen or window can start.</summary>
    private static object Create(Rig rig, Type viewModel)
    {
        object[] fakes = [new FakeNavigation(), new SilentViewer()];
        var wanted = viewModel.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        return Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance(rig.Provider, viewModel, [.. fakes.Where(f => wanted.Any(t => t.IsInstanceOfType(f)))]);
    }

    /// <summary>Stands in for the dialog window: the dialog's own view is rendered off-screen (no window, no popup) so the test can see what the user would, and the dialog never closes.</summary>
    private sealed class OffscreenDialogs : IFormDialogPresenter, IDisposable
    {
        public List<Hosted> Hosts { get; } = [];

        public Task ShowAsync(DialogViewModel viewModel)
        {
            var content = new ContentControl { Content = viewModel, ContentTemplateSelector = Lifestream.Desktop.Services.ViewLocator.Instance, Focusable = false };
            Hosts.Add(Hosted.Create(content, 560, 640));
            return new TaskCompletionSource().Task;
        }

        public void Dispose() => Hosts.ForEach(h => h.Dispose());
    }

    /// <summary>One operation under test: its own rig, view model, rendered screen and silent server.</summary>
    private sealed class Operation(Screen screen, string commandName) : IDisposable
    {
        public Rig? Rig { get; set; }

        public ViewModelBase? Vm { get; set; }

        public Hosted? Host { get; set; }

        public OffscreenDialogs Dialogs { get; } = new();

        public SilentServer Server { get; } = new();

        public Screen Screen { get; } = screen;

        public string CommandName { get; } = commandName;

        /// <summary>True when the operation needs no inspection after the wait; <see cref="Problem"/> then holds the verdict (null when fine).</summary>
        public bool Finished { get; set; }

        public string? Problem { get; set; }

        /// <summary>When the server went into the waking state for this operation (the wake-aware handler does it once a call has waited 3 s).</summary>
        public long WakingSince { get; set; }

        public void Dispose()
        {
            Dialogs.Dispose();
            Host?.Dispose();
            Rig?.Dispose();
        }
    }

    /// <summary>Loads the screen, selects a record, makes the server stop answering and runs the command (it starts its API call and waits).</summary>
    private static Operation Start(Screen screen, string commandName)
    {
        var op = new Operation(screen, commandName);
        var rig = op.Rig = Part2Harness.NewRig();
        rig.Get<DialogService>().Presenter = new Yes();
        rig.Get<FormDialogService>().Presenter = op.Dialogs;
        var vm = op.Vm = (ViewModelBase)Create(rig, screen.ViewModel);
        vm.Attach(rig.Get<ServerStatus>(), TimeProvider.System);
        var host = op.Host = Hosted.Create(screen.View(vm), Width, Height);
        Hosted.Await(() => vm.LoadAsync());
        host.Settle(100);
        if (screen.SelectFirst)
        {
            Hosted.Await(() =>
            {
                var items = (System.Collections.IList)vm.GetType().GetProperty("Items")!.GetValue(vm)!;
                vm.GetType().GetProperty("SelectedItem")!.SetValue(vm, items[screen.SelectIndex]);
                return (Task)vm.GetType().GetProperty("PendingDetail", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
            });
            host.Settle(100);
        }

        screen.Prepare?.Invoke(vm);
        op.Server.Install(rig.Api);
        if (vm.GetType().GetProperty(commandName)!.GetValue(vm) is not AsyncRelayCommand command)
        {
            op.Finished = true; // this screen does not offer the command (the property is null)
            return op;
        }

        var parameter = screen.Parameters.TryGetValue(commandName, out var pick) ? pick(vm) : new[] { Selected(vm), null }.FirstOrDefault(p => command.CanExecute(p));
        if (!command.CanExecute(parameter))
        {
            op.Finished = true;
            op.Problem = NotRunnable.ContainsKey(commandName) ? null : $"{screen.Name}.{commandName} cannot run on a loaded screen with a selected record: it needs a reviewed entry";
            return op;
        }

        Hosted.Await(() =>
        {
            command.Execute(parameter); // started from the dispatcher, as a click is, so its continuations come back to the UI thread
            return Task.CompletedTask;
        });
        host.Settle(150);
        if (op.Server.Count == 0)
        {
            op.Finished = true;
            op.Problem = IsNoCall(screen.Name, commandName) ? null : $"{screen.Name}.{commandName} made no API call and is not in the reviewed no-call list";
        }
        else if (IsNoCall(screen.Name, commandName))
        {
            op.Finished = true;
            op.Problem = $"{screen.Name}.{commandName} is listed as making no API call but it called {op.Server.Waiting[0].Method}";
        }
        else
        {
            // Right away, so the operation's own 3 s timer (started with the command) finds the server waking whenever it fires, however long the other operations take to start.
            rig.Get<ServerStatus>().Enter();
            op.WakingSince = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        return op;
    }

    /// <summary>After the 3.6 s: what the user sees, then Cancel, then whether the HTTP requests stopped.</summary>
    private static string? Inspect(Operation op, string capture)
    {
        var (screen, commandName, server) = (op.Screen, op.CommandName, op.Server);
        List<Hosted> surfaces = [op.Host!, .. op.Dialogs.Hosts];
        foreach (var surface in surfaces)
        {
            surface.Settle(100);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(capture)!);
        File.WriteAllBytes(capture, op.Host!.Capture());
        var shown = surfaces.Select(WakingSurface).ToList();
        if (shown.All(w => w.Texts.Count == 0))
        {
            return $"{screen.Name}.{commandName}: after 3.6 s on a waking server the screen shows no 'Waking up the server' text (waiting: {string.Join(",", server.Waiting.Select(w => w.Method))})";
        }

        if (shown.All(w => w.Cancel is null))
        {
            return $"{screen.Name}.{commandName}: the waking text is shown but with no visible Cancel";
        }

        foreach (var (_, cancel) in shown.Where(w => w.Cancel is not null))
        {
            cancel!.Command?.Execute(null);
        }

        op.Host.Settle(250);
        List<(string Method, CancellationToken Token)> waiting;
        lock (server.Waiting)
        {
            waiting = [.. server.Waiting];
        }

        var notCancelled = waiting.Where(w => !w.Token.IsCancellationRequested).Select(w => w.Method).ToList();
        return notCancelled.Count == 0 ? null : $"{screen.Name}.{commandName}: Cancel did not cancel the HTTP request of {string.Join(",", notCancelled)}";
    }

    /// <summary>
    /// Undo is not a command property: it is the action of the toast a delete shows. The delete is allowed to succeed, then the server stops answering and the
    /// toast's Undo runs; the screen must show the waking notice with a Cancel that stops the restore request.
    /// </summary>
    [Theory]
    [InlineData("Donors")]
    [InlineData("Patients")]
    [InlineData("Staff")]
    public void UndoOfADelete_OnAServerThatNeverAnswers_ShowsWakingAndACancelThatStopsTheRequest_FakeApi(string screenName)
    {
        var screen = Screens.Single(s => s.Name == screenName);
        var capturesDirectory = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "captures");
        var problem = StaHost.Run(() =>
        {
            using var op = new Operation(screen, "Undo");
            var rig = op.Rig = Part2Harness.NewRig();
            rig.Get<DialogService>().Presenter = new Yes();
            var vm = op.Vm = (ViewModelBase)Create(rig, screen.ViewModel);
            vm.Attach(rig.Get<ServerStatus>(), TimeProvider.System);
            var host = op.Host = Hosted.Create(screen.View(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(100);
            Hosted.Await(() =>
            {
                var items = (System.Collections.IList)vm.GetType().GetProperty("Items")!.GetValue(vm)!;
                vm.GetType().GetProperty("SelectedItem")!.SetValue(vm, items[screen.SelectIndex]);
                return (Task)vm.GetType().GetProperty("PendingDetail", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
            });
            host.Settle(100);
            var toasts = rig.Get<ToastService>();
            var delete = (AsyncRelayCommand)vm.GetType().GetProperty("DeleteCommand")!.GetValue(vm)!;
            Hosted.Await(() =>
            {
                delete.Execute(null);
                return Task.CompletedTask;
            });
            Assert.True(Hosted.PumpUntil(() => toasts.Toasts.Any(t => t.HasAction), 5000), $"the delete did not finish with an Undo toast: toasts=[{string.Join(" | ", toasts.Toasts.Select(t => t.Message))}] calls=[{string.Join(",", rig.Api.Calls)}] canDelete={((AsyncRelayCommand)vm.GetType().GetProperty("DeleteCommand")!.GetValue(vm)!).CanExecute(null)}");

            op.Server.Install(rig.Api);
            var undo = toasts.Toasts.First(t => t.HasAction).ActionCommand;
            Hosted.Await(() =>
            {
                undo.Execute(null);
                return Task.CompletedTask;
            });
            host.Settle(150);
            Assert.True(op.Server.Count > 0, "Undo made no API call");
            rig.Get<ServerStatus>().Enter();
            Hosted.RunDispatcherFor(3600);
            return Inspect(op, Path.Combine(capturesDirectory, "waking", $"{screen.Name}_Undo.png"));
        });

        Assert.True(problem is null, problem);
    }

    /// <summary>The verifier's probe (v-12-shell, Probe_DonorsDelete_OnAWakingServer_ShowsWakingAndCancel) as a test: Delete on Donors, real view, server waking, 3.6 s.</summary>
    [Fact]
    public void DonorsDelete_OnAWakingServer_ShowsWakingAndACancelThatStopsTheDelete_FakeApi()
    {
        var capturesDirectory = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "captures");
        var problem = StaHost.Run(() =>
        {
            using var op = Start(Screens.Single(s => s.Name == "Donors"), "DeleteCommand");
            Assert.False(op.Finished, op.Problem);
            Assert.Contains("DeleteDonorAsync", op.Server.Waiting.Select(w => w.Method));
            op.Rig!.Get<ServerStatus>().Enter();
            Hosted.RunDispatcherFor(3600);
            return Inspect(op, Path.Combine(capturesDirectory, "waking", "DonorsDelete_Waking_3_6s.png"));
        });

        Assert.True(problem is null, problem);
    }

    [Fact]
    public void EveryCommandOfEveryScreen_OnAServerThatNeverAnswers_ShowsWakingAndACancelThatStopsTheRequest_FakeApi()
    {
        var capturesDirectory = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "captures");
        var (problems, exercised, withCalls) = StaHost.Run(() =>
        {
            var found = new List<string>();
            var exercised = 0;
            var withCalls = 0;
            foreach (var screen in Screens)
            {
                // All commands of a screen are started together, each on its own screen instance and silent server, so the real 3.6 s of waiting are shared.
                var operations = new List<Operation>();
                try
                {
                    foreach (var command in CommandsOf(screen.ViewModel).Where(p => !p.IsDefined(typeof(NoOperationRunnerAttribute), false) || IsNoCall(screen.Name, p.Name)))
                    {
                        operations.Add(Start(screen, command.Name));
                    }

                    // Every operation has now been waking for at least 3.6 s: wait out what is left for the one that went last.
                    var lastWaking = operations.Where(o => !o.Finished).Select(o => o.WakingSince).DefaultIfEmpty(System.Diagnostics.Stopwatch.GetTimestamp()).Max();
                    var left = 3600 - (int)System.Diagnostics.Stopwatch.GetElapsedTime(lastWaking).TotalMilliseconds;
                    if (left > 0)
                    {
                        Hosted.RunDispatcherFor(left);
                    }

                    foreach (var op in operations)
                    {
                        exercised++;
                        if (!IsNoCall(screen.Name, op.CommandName))
                        {
                            withCalls++;
                        }

                        var problem = op.Finished ? op.Problem : Inspect(op, Path.Combine(capturesDirectory, "waking", $"{screen.Name}_{op.CommandName}.png"));
                        if (problem is not null)
                        {
                            found.Add(problem);
                        }
                    }
                }
                finally
                {
                    operations.ForEach(o => o.Dispose());
                }
            }

            return (found, exercised, withCalls);
        });

        Assert.True(exercised > 40 && withCalls > 15, $"only {exercised} commands ({withCalls} calling the API) were exercised: the inventory is not walking the screens");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
