using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Dialogs;
using Lifestream.Desktop.Features.Profile;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-24 (the window's X on a form dialog) and the date picker that must not offer days the form refuses; nothing is ever shown.</summary>
[Collection("Wpf")]
public sealed class DialogAndFieldViewTests
{
    [Fact]
    public void TheDialogWindowsX_OnADirtyDialog_AsksDiscardChanges_StaysOpenWhenRefused_AndClosesWhenAgreed()
    {
        var (openAfterRefuse, asked, closedAfterAgree, askedTitles) = StaHost.Run(() =>
        {
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            var dialogs = new FakeDialogs { Answer = false };
            using var vm = new ChangePasswordDialogViewModel(rig.Get<ILifestreamApiClient>());
            vm.Bind(null, TimeProvider.System, dialogs);
            vm.NewPassword = TestSecrets.TypedNotSaved;
            var window = new FormDialogWindow { DataContext = vm };
            vm.CloseRequested += (_, _) => window.Close(); // what the presenter does
            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Close(); // the X (and Alt+F4)
            Hosted.PumpUntil(() => dialogs.Confirmations.Count > 0, 2000);
            StaHost.Settle(100);
            var stillOpen = !closed;
            var count = dialogs.Confirmations.Count;

            dialogs.Answer = true;
            window.Close();
            Hosted.PumpUntil(() => closed, 2000);
            return (stillOpen, count, closed, dialogs.Confirmations.Select(c => c.Title).Distinct().ToList());
        });

        Assert.True(openAfterRefuse, "the dialog closed although the user refused to discard");
        Assert.Equal(1, asked);
        Assert.True(closedAfterAgree);
        Assert.Equal(["Discard changes?"], askedTitles);
    }

    [Fact]
    public void TheDialogWindowsX_OnACleanDialog_ClosesWithoutAsking()
    {
        var (closed, asked) = StaHost.Run(() =>
        {
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            var dialogs = new FakeDialogs();
            using var vm = new ChangePasswordDialogViewModel(rig.Get<ILifestreamApiClient>());
            vm.Bind(null, TimeProvider.System, dialogs);
            var window = new FormDialogWindow { DataContext = vm };
            vm.CloseRequested += (_, _) => window.Close();
            var done = false;
            window.Closed += (_, _) => done = true;

            window.Close();
            Hosted.PumpUntil(() => done, 2000);
            return (done, dialogs.Confirmations.Count);
        });

        Assert.True(closed);
        Assert.Equal(0, asked);
    }

    // ---- the date picker -------------------------------------------------------------------------------------------------------

    private static (bool PastBlocked, bool ChosenKept, bool TodayOpen, bool FutureOpen, bool OldChosenBlockedAfterPickingFuture) PastDaysInTheCalendar()
    {
        return StaHost.Run(() =>
        {
            _ = ThemeLoader.Resources;
            var today = new DateOnly(2026, 11, 19);
            var original = today.AddDays(-10); // an appointment whose day has passed: a reschedule opens with that date chosen
            var field = new DateField { MinDate = today, SelectedDate = original };
            using var host = Hosted.Create(field, 320, 80);
            host.Settle(200);
            var picker = (Calendar)field.FindName("Picker");
            DateTime At(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

            var pastBlocked = Enumerable.Range(1, 400).Where(n => n != 10).All(n => picker.BlackoutDates.Contains(At(today.AddDays(-n))));
            var chosenKept = !picker.BlackoutDates.Contains(At(original)) && picker.SelectedDate == At(original);
            var todayOpen = !picker.BlackoutDates.Contains(At(today));
            var futureOpen = !picker.BlackoutDates.Contains(At(today.AddDays(30)));

            picker.SelectedDate = At(today.AddDays(3)); // the user clicks a day in the future
            host.Settle(100);
            var oldBlocked = picker.BlackoutDates.Contains(At(original));
            return (pastBlocked, chosenKept, todayOpen, futureOpen, oldBlocked);
        });
    }

    [Fact]
    public void Calendar_OfARescheduleWhosePastDateIsChosen_BlocksEveryOtherPastDay_ButNotTodayOrLater()
    {
        var (pastBlocked, chosenKept, todayOpen, futureOpen, _) = PastDaysInTheCalendar();

        Assert.True(pastBlocked, "a past day other than the chosen one can be clicked");
        Assert.True(chosenKept, "the date the appointment already has must stay as it is");
        Assert.True(todayOpen);
        Assert.True(futureOpen);
    }

    [Fact]
    public void Calendar_OnceAFutureDayIsPicked_TheOldPastDateIsBlockedToo()
    {
        var (_, _, _, _, oldBlocked) = PastDaysInTheCalendar();

        Assert.True(oldBlocked);
    }

    [Fact]
    public void Calendar_WithAMaxDate_BlocksEveryLaterDay()
    {
        var blocked = StaHost.Run(() =>
        {
            _ = ThemeLoader.Resources;
            var today = new DateOnly(2026, 11, 19);
            var field = new DateField { MaxDate = today, SelectedDate = today.AddYears(-30) };
            using var host = Hosted.Create(field, 320, 80);
            host.Settle(200);
            var picker = (Calendar)field.FindName("Picker");
            return Enumerable.Range(1, 200).All(n => picker.BlackoutDates.Contains(today.AddDays(n).ToDateTime(TimeOnly.MinValue))) && !picker.BlackoutDates.Contains(today.ToDateTime(TimeOnly.MinValue));
        });

        Assert.True(blocked);
    }

    // ---- the waking notice, rendered ------------------------------------------------------------------------------------------

    [Fact]
    public void WakingNotice_ShowsInTheDialog_WhileTheSubmitWaitsOnAWakingServer_AndItsCancelButtonStopsTheRequest_FakeApi()
    {
        var (before, visible, cancelled, message) = StaHost.Run(() =>
        {
            var api = new FakeApiClient();
            var observed = new List<CancellationToken>();
            var never = new TaskCompletionSource<object>();
            api.Handlers["ChangePasswordAsync"] = ct =>
            {
                observed.Add(ct);
                return never.Task.WaitAsync(ct);
            };
            var clock = new ManualTimeProvider();
            var status = new ServerStatus();
            using var vm = new ChangePasswordDialogViewModel(ScopedApiClient.Wrap(api, strict: true));
            vm.Bind(status, clock, new FakeDialogs());
            vm.CurrentPassword = TestSecrets.Old123;
            vm.NewPassword = TestSecrets.New456;
            vm.ConfirmPassword = TestSecrets.New456;
            using var host = Hosted.Create(new ChangePasswordDialogView { DataContext = vm }, 560, 520);
            host.Settle(100);
            var notice = host.All<WakingNotice>().Single();
            var hidden = !notice.IsVisible; // nothing is running yet

            _ = vm.PrimaryCommand.ExecuteAsync();
            Hosted.PumpUntil(() => observed.Count == 1, 2000);
            status.Enter();
            clock.Advance(TimeSpan.FromSeconds(3.5));
            host.Settle(300);
            var shown = notice.IsVisible && vm.IsOperationWaking;

            host.All<Button>().First(b => b.Name == "CancelWakingButton").Command!.Execute(null);
            Hosted.PumpUntil(() => !vm.PrimaryCommand.IsRunning, 2000);
            host.Settle(100);
            return (hidden, shown, observed.Single().IsCancellationRequested, vm.FormError);
        });

        Assert.True(before, "the notice shows although nothing waits on the server");
        Assert.True(visible, "the notice is not shown after 3 s of waiting on a waking server");
        Assert.True(cancelled, "the notice's Cancel did not cancel the request");
        Assert.Contains("cancelled", message, StringComparison.OrdinalIgnoreCase);
    }
}
