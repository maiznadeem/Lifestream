using System.Diagnostics;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Contracts.Dashboard;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Dashboard;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// FEAT-27 (PERF-16): the FIRST dashboard of a process, which is the one the user sees after signing in, draws its charts within 300 ms of the data arriving.
/// The clock starts when the data is released, so applying it to the screen is inside the measurement (the earlier proof started after that).
/// Run it alone for a cold process: <c>dotnet test --filter Category=ColdPerf</c>. Each run appends a line to <c>cold-dashboard.txt</c> in the capture folder saying whether it was
/// the first chart in the process. The app starts the chart warm-up at startup (App.OnStartup, off the UI thread), well before the dashboard exists; the test does the same and
/// then waits for it, as the login screen and the sign-in call do in the app.
/// </summary>
[Collection("Wpf")]
public sealed class ColdDashboardRenderTests
{
    private const int Width = 1366;
    private const int Height = 736;

    private static bool ChartsDrawn(byte[] png) =>
        StaHost.CountPixelsNear(png, 0xFF, 0x7A, 0x8A, 3) > 400 && StaHost.CountPixelsNear(png, 0x7C, 0xB8, 0xFF, 3) > 400;

    [Fact]
    [Trait("Category", "ColdPerf")]
    public void Dashboard_FirstChartRenderOfTheProcess_FinishesWithin300ms_OfTheDataArriving_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            var startedWarmUp = Stopwatch.StartNew();
            var warmUp = ChartWarmUp.Start(System.Windows.Threading.Dispatcher.CurrentDispatcher); // what App.OnStartup does: queued at idle priority on the UI thread
            using var rig = FeatureHarness.NewRig();
            var gate = new TaskCompletionSource();
            var data = DemoData.Dashboard(12); // built before the clock starts, as the response is already parsed when it arrives
            rig.Api.On("GetDashboardAsync", async _ =>
            {
                await gate.Task;
                return ApiResult<DashboardResponse>.Ok(data);
            });
            var vm = FeatureHarness.Create<DashboardViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.DashboardView(vm), Width, Height);
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));

            var drawsBefore = DashboardView.ChartDrawCount; // 0 when this is the first dashboard in the process
            var load = vm.LoadAsync();
            Hosted.PumpUntil(() => host.All<Skeleton>().Any(s => s.IsVisible), 1000);
            Hosted.PumpUntil(() => warmUp.IsCompleted, 10_000); // in the app the sign-in window and the sign-in call take far longer than this
            var warmedAfterMs = startedWarmUp.ElapsedMilliseconds;

            var arrival = Stopwatch.StartNew();
            gate.SetResult();
            Hosted.PumpUntil(() => load.IsCompleted, 3000);
            var appliedMs = arrival.ElapsedMilliseconds; // the data is on the screen's model; the charts are drawn next
            var drawn = Hosted.PumpUntil(() => ChartsDrawn(host.Capture()), 3000);
            var drawnMs = arrival.ElapsedMilliseconds;
            FeatureHarness.Save(host, "ColdDashboard_Loaded.png");
            return (drawn, drawnMs, appliedMs, drawsBefore, warmedAfterMs);
        });

        var directory = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } fromEnvironment ? fromEnvironment : FeatureHarness.CaptureDirectory;
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "cold-dashboard.txt"),
            $"{DateTime.UtcNow:O} firstChartInProcess={facts.drawsBefore == 0} chartsDrawn={facts.drawn} appliedMs={facts.appliedMs} drawnMsAfterDataReleased={facts.drawnMs} warmUpCompletedAfterMs={facts.warmedAfterMs}{Environment.NewLine}");

        Assert.True(facts.drawn, "the charts were not drawn");
        Assert.True(facts.drawnMs < 300, $"the charts took {facts.drawnMs} ms after the data arrived (first chart in the process: {facts.drawsBefore == 0})");
    }

    [Fact]
    public void TheApp_StartsTheChartWarmUp_WhenItStarts()
    {
        var app = File.ReadAllText(Path.Combine(Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop"), "App.cs"));

        Assert.Contains("ChartWarmUp.Start(Dispatcher)", app, StringComparison.Ordinal);
        Assert.True(app.IndexOf("ChartWarmUp.Start(", StringComparison.Ordinal) < app.IndexOf("IAppFlow>().Start()", StringComparison.Ordinal), "the warm-up must be queued before the sign-in flow starts");
    }
}
