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
/// The single-operation tests (Undo, Delete) run on a manual clock (the clock of the screen, its toasts and dialogs) stepped 3.6 s past the start of the
/// call; the walk of every command keeps the real clock, because its screens also run timers of their own (thumbnail loads, slot lookups) whose
/// order against the waking threshold is part of what it covers.
/// </summary>
[Collection("Wpf")]
public sealed class WakingEveryOperationTests
{
    private const int Width = 1366;
    private const int Height = 736;

    /// <summary>The state the screen is in when the command runs: SHELL-07 must hold in every one of them, not only with a record selected.</summary>
    public enum ScreenState
    {
        NoSelection,
        RecordSelected,
        NewEditor,
        EditEditor,
        DialogOpen,

        /// <summary>
        /// Only for the action of a toast (Undo): the toast outlives the screen that showed it, so the user may have gone to another screen
        /// (whose view model is a different one, and the first is disposed) when they press it. Not a state commands are walked in.
        /// </summary>
        NavigatedAway,
    }

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
        [nameof(StaffViewModel.ResetPasswordCommand)] = "opens the reset-password dialog",
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
        [nameof(ProfileViewModel.ChangePasswordCommand)] = "opens the password dialog",
        ["ClosePanelCommand"] = "closes the side panel (clears the selection; Staff asks first when its form has changes)",
        ["ChooseBloodTypeCommand"] = "the blood group chip's menu: with no group chosen it does nothing; a chosen group goes through the filter (Receipts reloads, Stock filters what is loaded)",
        ["Stock.FindDonorsCommand"] = "navigates to Donors with the group (a fake navigation here)",
        ["Stock.OpenRequestsCommand"] = "navigates to Requests with the group (a fake navigation here)",
        ["Receipts.PrintCommand"] = "prints the receipt already loaded in the panel (the printer, no API call)",
        ["Receipts.SavePdfCommand"] = "saves the receipt already loaded in the panel as a PDF (the printer, no API call)",
        ["Patients.FulfilRequestCommand"] = "navigates to Requests with the request to fulfil (a fake navigation here; the fulfil step is covered by the Requests tests)",
        ["Appointments.CheckInCommand"] = "selects the row, whose detail is already loaded in this state (selecting a row is covered by the detail tests)",
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

    /// <summary>True when the text runs past the right edge of the control that holds it (the notice, the banner), so the user would see it cut off.</summary>
    private static bool Clipped(TextBlock text, FrameworkElement root)
    {
        DependencyObject? holder = VisualTreeHelper.GetParent(text);
        while (holder is not null and not Border { ActualWidth: > 0 })
        {
            holder = VisualTreeHelper.GetParent(holder);
        }

        return holder is Border border && Hosted.BoundsOf(text, border).Right > border.ActualWidth + 0.5 && root.IsVisible;
    }

    /// <summary>The visible "Waking up the server" texts and the visible Cancel button that sits with one of them (within three levels of containers).</summary>
    private static (List<TextBlock> Texts, Button? Cancel, List<Button> Cancels) WakingSurface(Hosted host)
    {
        var texts = host.All<TextBlock>().Where(t => t.IsVisible && (t.Text ?? string.Empty).Contains("Waking up the server", StringComparison.Ordinal)).ToList();
        var clipped = texts.FirstOrDefault(t => Clipped(t, host.Element));
        if (clipped is not null)
        {
            texts = []; // text that runs past the edge of what holds it is not something the user can read: it counts as not shown
        }

        // Every waking notice on the surface has its own Cancel (the shell's notice for a toast's action, the screen's for its own operations).
        List<Button> cancels = [];
        foreach (var text in texts)
        {
            DependencyObject? level = text;
            for (var up = 0; up < 3 && level is not null; up++)
            {
                level = VisualTreeHelper.GetParent(level);
                var cancel = level is null ? null : Hosted.Descendants(level).OfType<Button>().FirstOrDefault(b => b.IsVisible && b.Content as string == "Cancel");
                if (cancel is not null)
                {
                    if (!cancels.Contains(cancel))
                    {
                        cancels.Add(cancel);
                    }

                    break;
                }
            }
        }

        return (texts, cancels.FirstOrDefault(), cancels);
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
    private sealed class Operation(Screen screen, string commandName, ScreenState state = ScreenState.RecordSelected, bool realTime = false) : IDisposable
    {
        public ScreenState State { get; } = state;

        public Rig? Rig { get; set; }

        public ViewModelBase? Vm { get; set; }

        public Hosted? Host { get; set; }

        public OffscreenDialogs Dialogs { get; } = new();

        public SilentServer Server { get; } = new();

        /// <summary>The operation's manual clock (the screen, its runner, the toasts and the dialogs run on it, and the test steps it over the waking threshold); null on the real clock.</summary>
        public ManualTimeProvider? Manual { get; } = realTime ? null : new(DateTimeOffset.UtcNow);

        public TimeProvider Clock => (TimeProvider?)Manual ?? TimeProvider.System;

        public Screen Screen { get; } = screen;

        public string CommandName { get; } = commandName;

        /// <summary>True when the operation needs no inspection after the wait; <see cref="Problem"/> then holds the verdict (null when fine).</summary>
        public bool Finished { get; set; }

        public string? Problem { get; set; }

        /// <summary>True when the command could not run in this state (no such state on the screen, the command is disabled, or it made no call): it is not counted as exercised.</summary>
        public bool NotApplicable { get; set; }

        /// <summary>When the server went into the waking state for this operation (the wake-aware handler does it once a call has waited 3 s).</summary>
        public long WakingSince { get; set; }

        public void Dispose()
        {
            Dialogs.Dispose();
            Host?.Dispose();
            Rig?.Dispose();
        }
    }

    /// <summary>Puts a loaded screen into <paramref name="op"/>'s state. False when the screen has no such state (nothing to select, or no editor opens).</summary>
    private static bool ArrangeState(Operation op, ViewModelBase vm, Hosted host)
    {
        var screen = op.Screen;
        var state = op.State;
        var type = vm.GetType();
        if (state == ScreenState.NoSelection && !screen.SelectFirst)
        {
            return false; // a screen without a selection is always in this state: RecordSelected covers it
        }

        if (state is ScreenState.RecordSelected or ScreenState.EditEditor or ScreenState.DialogOpen && screen.SelectFirst)
        {
            SelectRow(vm, screen.SelectIndex);
            host.Settle(100);
        }

        switch (state)
        {
            case ScreenState.NewEditor:
            case ScreenState.EditEditor:
                if (type.GetProperty(state == ScreenState.NewEditor ? "AddCommand" : "EditCommand")?.GetValue(vm) is not AsyncRelayCommand open || !open.CanExecute(null))
                {
                    return false;
                }

                Hosted.Await(() =>
                {
                    open.Execute(null);
                    return Task.CompletedTask;
                });
                Hosted.PumpUntil(() => type.GetProperty("Editor")?.GetValue(vm) is not null, 5000);
                host.Settle(150);
                return type.GetProperty("Editor")?.GetValue(vm) is not null;
            case ScreenState.DialogOpen:
                _ = op.Dialogs.ShowAsync(new ChangePasswordDialogViewModel(op.Rig!.Api));
                host.Settle(100);
                return true;
            default:
                return true;
        }
    }

    private static void SelectRow(ViewModelBase vm, int index) =>
        Hosted.Await(() =>
        {
            var items = (System.Collections.IList)vm.GetType().GetProperty("Items")!.GetValue(vm)!;
            vm.GetType().GetProperty("SelectedItem")!.SetValue(vm, items[index]);
            return (Task)vm.GetType().GetProperty("PendingDetail", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        });

    /// <summary>Creates the rig, the view model and the rendered screen, loads it and puts it in the operation's state. False when the screen has no such state.</summary>
    private static bool Open(Operation op)
    {
        var screen = op.Screen;
        var rig = op.Rig = Part2Harness.NewRig(clock: op.Manual);
        rig.Get<DialogService>().Presenter = new Yes();
        rig.Get<FormDialogService>().Presenter = op.Dialogs;
        var vm = op.Vm = (ViewModelBase)Create(rig, screen.ViewModel);
        vm.Attach(rig.Get<ServerStatus>(), op.Clock);
        var host = op.Host = Hosted.Create(screen.View(vm), Width, Height);
        Hosted.Await(() => vm.LoadAsync());
        host.Settle(100);
        if (!ArrangeState(op, vm, host))
        {
            return false;
        }

        screen.Prepare?.Invoke(vm);
        return true;
    }

    /// <summary>The commands a user could press with the screen in this state (the others are disabled there), found on one throwaway instance so the walk does not build a screen for each disabled command.</summary>
    private static HashSet<string> Runnable(Screen screen, ScreenState state)
    {
        using var probe = new Operation(screen, "probe", state, realTime: true);
        var found = new HashSet<string>();
        if (!Open(probe))
        {
            return found;
        }

        foreach (var property in CommandsOf(screen.ViewModel))
        {
            if (property.GetValue(probe.Vm) is AsyncRelayCommand command && (screen.Parameters.TryGetValue(property.Name, out var pick) ? command.CanExecute(pick(probe.Vm!)) : new[] { Selected(probe.Vm!), null }.Any(command.CanExecute)))
            {
                found.Add(property.Name);
            }
        }

        return found;
    }

    /// <summary>Loads the screen, puts it in the state, makes the server stop answering and runs the command (it starts its API call and waits).</summary>
    private static Operation Start(Screen screen, string commandName, ScreenState state = ScreenState.RecordSelected, bool realTime = false)
    {
        var op = new Operation(screen, commandName, state, realTime);
        var strict = state == ScreenState.RecordSelected; // the reviewed lists describe the selected-record state; elsewhere a disabled or silent command is simply not applicable
        var arranged = Open(op);
        var rig = op.Rig!;
        var vm = op.Vm!;
        var host = op.Host!;
        if (!arranged)
        {
            op.Finished = true; // this screen has no such state (no selection, no editor)
            op.NotApplicable = true;
            return op;
        }

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
            op.NotApplicable = !strict;
            op.Problem = !strict || NotRunnable.ContainsKey(commandName) ? null : $"{screen.Name}.{commandName} cannot run on a loaded screen with a selected record: it needs a reviewed entry";
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
            op.Problem = !strict || IsNoCall(screen.Name, commandName) ? null : $"{screen.Name}.{commandName} made no API call and is not in the reviewed no-call list";
            op.NotApplicable = !strict;
        }
        else if (strict && IsNoCall(screen.Name, commandName))
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

    /// <summary>Steps the operation's clock 3.6 s past the start of its call (the waking threshold is 3 s), then <see cref="Inspect"/>.</summary>
    private static string? InspectAfterWakingThreshold(Operation op, string capture)
    {
        op.Manual!.Advance(TimeSpan.FromMilliseconds(3600));
        return Inspect(op, capture);
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

        foreach (var cancel in shown.SelectMany(w => w.Cancels))
        {
            cancel.Command?.Execute(null);
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
    /// Undo is not a command property: it is the action of the toast a delete shows. The delete is allowed to succeed, then the screen is put in the state
    /// (nothing selected, a new or an edit editor open, a dialog open), the server stops answering and the toast's Undo runs; the screen must show the
    /// waking notice with a Cancel that stops the restore request. The editor and dialog cases are the verifier's probe (v-12c, Probe_UndoWhileEditorOpen).
    /// </summary>
    [Theory]
    [InlineData("Donors", ScreenState.NoSelection)]
    [InlineData("Donors", ScreenState.NewEditor)]
    [InlineData("Donors", ScreenState.EditEditor)]
    [InlineData("Donors", ScreenState.DialogOpen)]
    [InlineData("Patients", ScreenState.NoSelection)]
    [InlineData("Patients", ScreenState.NewEditor)]
    [InlineData("Patients", ScreenState.EditEditor)]
    [InlineData("Patients", ScreenState.DialogOpen)]
    [InlineData("Staff", ScreenState.NoSelection)]
    [InlineData("Staff", ScreenState.NewEditor)]
    [InlineData("Staff", ScreenState.EditEditor)]
    [InlineData("Staff", ScreenState.DialogOpen)]
    [InlineData("Donors", ScreenState.NavigatedAway)]
    [InlineData("Patients", ScreenState.NavigatedAway)]
    [InlineData("Staff", ScreenState.NavigatedAway)]
    public void UndoOfADelete_OnAServerThatNeverAnswers_ShowsWakingAndACancelThatStopsTheRequest_FakeApi(string screenName, ScreenState state)
    {
        var screen = Screens.Single(s => s.Name == screenName);
        var capturesDirectory = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "captures");
        var problem = StaHost.Run(() =>
        {
            // The real shell, as the user has it: the toast and its waking notice live in the shell, the screen is the page it shows.
            using var op = new Operation(screen, "Undo", state);
            var rig = op.Rig = Part2Harness.NewRig(clock: op.Manual);
            rig.Get<DialogService>().Presenter = new Yes();
            rig.Get<FormDialogService>().Presenter = op.Dialogs;
            var shell = rig.Get<Lifestream.Client.Shell.ShellViewModel>();
            var nav = rig.Get<Lifestream.Client.Navigation.INavigationService>();
            var host = op.Host = Hosted.Create(new Lifestream.Desktop.Shell.ShellView { DataContext = shell }, Width, Height);
            Hosted.Await(() => nav.NavigateTo(screen.ViewModel));
            host.Settle(150);
            var vm = op.Vm = nav.Current!;
            SelectRow(vm, screen.SelectIndex);
            host.Settle(100);
            var toasts = rig.Get<ToastService>();
            var delete = (AsyncRelayCommand)vm.GetType().GetProperty("DeleteCommand")!.GetValue(vm)!;
            Hosted.Await(() =>
            {
                delete.Execute(null);
                return Task.CompletedTask;
            });
            Assert.True(Hosted.PumpUntil(() => toasts.Toasts.Any(t => t.HasAction), 5000), $"the delete did not finish with an Undo toast: toasts=[{string.Join(" | ", toasts.Toasts.Select(t => t.Message))}] calls=[{string.Join(",", rig.Api.Calls)}]");

            if (state == ScreenState.NavigatedAway)
            {
                // The user leaves for another screen (the first one is disposed) and presses Undo there.
                var other = screen.ViewModel == typeof(PatientsViewModel) ? typeof(DonorsViewModel) : typeof(PatientsViewModel);
                Hosted.Await(() => nav.NavigateTo(other));
                host.Settle(250);
                Assert.IsType(other, nav.Current);
                Assert.NotSame(vm, nav.Current);
            }

            // The user moves on while the Undo toast is still up: an editor or a dialog opens (the detail pane that used to hold the notice is collapsed or covered).
            if (state == ScreenState.DialogOpen)
            {
                _ = op.Dialogs.ShowAsync(new ChangePasswordDialogViewModel(rig.Api));
                host.Settle(100);
            }
            else if (state is ScreenState.NewEditor or ScreenState.EditEditor)
            {
                if (state == ScreenState.EditEditor)
                {
                    SelectRow(vm, screen.SelectIndex == 0 ? 1 : 0);
                    host.Settle(100);
                }

                var open = (AsyncRelayCommand)vm.GetType().GetProperty(state == ScreenState.NewEditor ? "AddCommand" : "EditCommand")!.GetValue(vm)!;
                Assert.True(open.CanExecute(null), $"{state}: the command that opens the editor is disabled");
                Hosted.Await(() =>
                {
                    open.Execute(null);
                    return Task.CompletedTask;
                });
                Hosted.PumpUntil(() => vm.GetType().GetProperty("Editor")!.GetValue(vm) is not null, 5000);
                host.Settle(150);
                Assert.NotNull(vm.GetType().GetProperty("Editor")!.GetValue(vm));
            }

            op.Server.Install(rig.Api);
            var undoButton = host.All<Button>().FirstOrDefault(b => b.IsVisible && b.Content as string == "Undo");
            Assert.True(undoButton is not null, "the Undo button of the toast is not on screen");
            ((System.Windows.Automation.Provider.IInvokeProvider)System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(undoButton)!).Invoke();
            host.Settle(150);
            Assert.True(op.Server.Count > 0, "Undo made no API call");
            rig.Get<ServerStatus>().Enter();
            return InspectAfterWakingThreshold(op, Path.Combine(capturesDirectory, "waking", $"{screen.Name}_Undo_{state}.png"));
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
            return InspectAfterWakingThreshold(op, Path.Combine(capturesDirectory, "waking", "DonorsDelete_Waking_3_6s.png"));
        });

        Assert.True(problem is null, problem);
    }

    /// <summary>The walk's result for some of the screen/state groups (what one walker process reports to the test).</summary>
    internal sealed record WalkResult(List<string> Problems, Dictionary<ScreenState, int> Exercised, Dictionary<ScreenState, int> WithCalls);

    /// <summary>How many screen/state groups the walk has.</summary>
    internal static int WalkGroupCount => WalkGroups().Count;

    /// <summary>Walks every <paramref name="count"/>-th group from <paramref name="index"/> (one walker's share).</summary>
    internal static WalkResult WalkShare(int index, int count, string capturesDirectory) => Walk(WalkGroups().Where((_, i) => i % count == index), capturesDirectory);

    private static List<(Screen Screen, ScreenState State)> WalkGroups() =>
        [.. from screen in Screens from state in Enum.GetValues<ScreenState>().Where(s => s != ScreenState.NavigatedAway) select (screen, state)];

    /// <summary>Walks the given screen/state groups on one STA thread, exactly as the whole walk used to run in one go.</summary>
    private static WalkResult Walk(IEnumerable<(Screen Screen, ScreenState State)> groups, string capturesDirectory) => StaHost.Run(() =>
    {
        var found = new List<string>();
        var exercised = Enum.GetValues<ScreenState>().ToDictionary(s => s, _ => 0);
        var withCalls = Enum.GetValues<ScreenState>().ToDictionary(s => s, _ => 0);
        foreach (var (screen, state) in groups)
        {
            // All commands of a screen in one state are started together, each on its own screen instance and silent server, so the real 3.6 s of waiting are shared.
            var operations = new List<Operation>();
            try
            {
                var runnable = state == ScreenState.RecordSelected ? null : Runnable(screen, state); // with a record selected every command is walked, so a disabled one needs a reviewed entry
                foreach (var command in CommandsOf(screen.ViewModel).Where(p => !p.IsDefined(typeof(NoOperationRunnerAttribute), false) || IsNoCall(screen.Name, p.Name)))
                {
                    if (runnable is null || runnable.Contains(command.Name))
                    {
                        operations.Add(Start(screen, command.Name, state, realTime: true));
                    }
                }

                // Every operation has now been waking for at least 3.6 s: wait out what is left for the one that went last.
                var lastWaking = operations.Where(o => !o.Finished).Select(o => o.WakingSince).DefaultIfEmpty(System.Diagnostics.Stopwatch.GetTimestamp()).Max();
                var left = 3600 - (int)System.Diagnostics.Stopwatch.GetElapsedTime(lastWaking).TotalMilliseconds;
                if (left > 0 && operations.Any(o => !o.Finished))
                {
                    Hosted.RunDispatcherFor(left);
                }

                foreach (var op in operations)
                {
                    if (op.NotApplicable)
                    {
                        continue; // no such state on this screen, or the command is disabled or makes no call in it
                    }

                    exercised[state]++;
                    if (!IsNoCall(screen.Name, op.CommandName))
                    {
                        withCalls[state]++;
                    }

                    var problem = op.Finished ? op.Problem : Inspect(op, Path.Combine(capturesDirectory, "waking", $"{screen.Name}_{state}_{op.CommandName}.png"));
                    if (problem is not null)
                    {
                        found.Add($"[{state}] {problem}");
                    }
                }
            }
            finally
            {
                operations.ForEach(o => o.Dispose());
            }
        }

        return new WalkResult(found, exercised, withCalls);
    });
}

/// <summary>
/// SHELL-07, the behavioural inventory of <see cref="WakingEveryOperationTests"/> over every command of every screen in every screen state. The screens are
/// walked in walker processes (several at once: WPF cannot build views on several threads of one process, its resource package is not thread-safe), so
/// this test does no WPF work of its own and runs beside the other WPF tests rather than in their one-at-a-time collection.
/// </summary>
public sealed class WakingEveryOperationWalkTests
{
    /// <summary>Set in a walker process: "index/count|result file|parent process id". The walker walks every count-th group from index and writes its result there; it ends itself if the test process that started it is gone.</summary>
    private const string WalkerVariable = "LIFESTREAM_WAKING_WALKER";

    /// <summary>How many walker processes share the groups (the default: one per two processors, at least one).</summary>
    private static int Walkers => Environment.GetEnvironmentVariable("LIFESTREAM_WAKING_WALKERS") is { Length: > 0 } set && int.TryParse(set, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
        ? n
        : Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// Runs the walk in <paramref name="count"/> processes of this test assembly at once, each running only this test for its share of the groups
    /// (WPF cannot build views on several threads of one process: its resource package is not thread-safe). A walker that fails, hangs or reports
    /// nothing fails the test with its output.
    /// </summary>
    private static List<WakingEveryOperationTests.WalkResult> WalkInProcesses(int count)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Lifestream.Desktop.Tests.exe");
        var method = $"{typeof(WakingEveryOperationWalkTests).FullName}.{nameof(EveryCommandOfEveryScreen_InEveryScreenState_OnAServerThatNeverAnswers_ShowsWakingAndACancelThatStopsTheRequest_FakeApi)}";
        var walkers = Enumerable.Range(0, count).Select(index =>
        {
            var output = Path.Combine(Path.GetTempPath(), $"lifestream-walk-{Guid.NewGuid():N}.json");
            var start = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-method", method, "-noLogo", "-noColor" })
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment[WalkerVariable] = $"{index}/{count}|{output}|{Environment.ProcessId}";
            var process = System.Diagnostics.Process.Start(start)!;
            var log = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, e) => { lock (log) { log.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { lock (log) { log.AppendLine(e.Data); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return (Process: process, Output: output, Log: log, Index: index);
        }).ToList();

        var results = new List<WakingEveryOperationTests.WalkResult>();
        var failures = new List<string>();
        foreach (var walker in walkers)
        {
            using var process = walker.Process;
            if (!process.WaitForExit(TimeSpan.FromMinutes(30)))
            {
                process.Kill(entireProcessTree: true);
                failures.Add($"walker {walker.Index} did not finish in 30 minutes");
            }

            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(walker.Output))
            {
                lock (walker.Log)
                {
                    failures.Add($"walker {walker.Index} exited with {process.ExitCode} and reported {(File.Exists(walker.Output) ? "a result" : "nothing")}:{Environment.NewLine}{walker.Log}");
                }
            }
            else
            {
                results.Add(System.Text.Json.JsonSerializer.Deserialize<WakingEveryOperationTests.WalkResult>(File.ReadAllText(walker.Output))!);
            }

            File.Delete(walker.Output);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        return results;
    }

    /// <summary>A walker never outlives the test process that started it (if that process is killed, its walkers end within a second).</summary>
    private static void ExitWhenGone(int parentId) =>
        new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(1000);
                try
                {
                    using var parent = System.Diagnostics.Process.GetProcessById(parentId);
                    if (parent.HasExited)
                    {
                        Environment.Exit(3);
                    }
                }
                catch (ArgumentException)
                {
                    Environment.Exit(3); // no such process any more
                }
            }
        })
        { IsBackground = true, Name = "walker parent watch" }.Start();

    [Fact]
    public void EveryCommandOfEveryScreen_InEveryScreenState_OnAServerThatNeverAnswers_ShowsWakingAndACancelThatStopsTheRequest_FakeApi()
    {
        var capturesDirectory = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "captures");
        if (Environment.GetEnvironmentVariable(WalkerVariable) is { Length: > 0 } walker)
        {
            // This process is one of the walkers: walk its share of the groups and report; the test that started it asserts on the whole walk.
            var fields = walker.Split('|');
            var share = fields[0].Split('/').Select(n => int.Parse(n, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            ExitWhenGone(int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture));
            var mine = WakingEveryOperationTests.WalkShare(share[0], share[1], capturesDirectory);
            File.WriteAllText(fields[1], System.Text.Json.JsonSerializer.Serialize(mine));
            return;
        }

        var parts = WalkInProcesses(Math.Min(Walkers, WakingEveryOperationTests.WalkGroupCount));
        var problems = parts.SelectMany(p => p.Problems).ToList();
        var exercised = Enum.GetValues<WakingEveryOperationTests.ScreenState>().ToDictionary(s => s, s => parts.Sum(p => p.Exercised[s]));
        var withCalls = Enum.GetValues<WakingEveryOperationTests.ScreenState>().ToDictionary(s => s, s => parts.Sum(p => p.WithCalls[s]));

        Assert.True(exercised[WakingEveryOperationTests.ScreenState.RecordSelected] > 40 && withCalls[WakingEveryOperationTests.ScreenState.RecordSelected] > 15, $"only {exercised[WakingEveryOperationTests.ScreenState.RecordSelected]} commands ({withCalls[WakingEveryOperationTests.ScreenState.RecordSelected]} calling the API) were exercised with a record selected: the inventory is not walking the screens");
        foreach (var state in new[] { WakingEveryOperationTests.ScreenState.NoSelection, WakingEveryOperationTests.ScreenState.NewEditor, WakingEveryOperationTests.ScreenState.EditEditor, WakingEveryOperationTests.ScreenState.DialogOpen })
        {
            Assert.True(withCalls[state] >= 3, $"only {withCalls[state]} commands calling the API were exercised in the {state} state: the inventory is not walking that state");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
