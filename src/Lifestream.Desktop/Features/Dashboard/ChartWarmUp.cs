using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Features.Dashboard;

/// <summary>
/// Pays the one-time cost of the first dashboard before anyone needs it (FEAT-27, PERF-16). The first dashboard of a process loads SkiaSharp's native library, parses the font, compiles
/// LiveCharts, the view model and the view and builds their templates. Measured in a fresh process, from the data arriving to the charts on screen: 283 to 319 ms without this, 217 to 239 ms
/// with it (about 130 ms of work, once, at idle priority). The app starts this at startup, while the sign-in window waits for the user, so the dashboard drawn after sign-in finds all of it
/// compiled. It draws the real dashboard view from a made-up response into a bitmap that nobody sees. An attempt on a thread of its own was less steady (some runs got slower), because the
/// charts share process-wide state with the page that will show them. A failure costs only the speed-up.
/// </summary>
public static class ChartWarmUp
{
    private static readonly Lock Gate = new();
    private static Task? _started;

    /// <summary>True while the warm-up draws its made-up dashboard (so a counter of real dashboards can leave it out).</summary>
    internal static bool IsRunning { get; private set; }

    /// <summary>
    /// Starts the warm-up once and returns the task that completes when it is finished (it never faults). It runs on the dispatcher at idle priority: the charts
    /// share process-wide state with the page that will show them, so they are warmed where that page lives, and only when the window has nothing else to do.
    /// </summary>
    public static Task Start(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        lock (Gate)
        {
            if (_started is not null)
            {
                return _started;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _started = done.Task;
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                try
                {
                    Warm();
                }
                catch (Exception ex)
                {
                    // Nothing depends on this: the first dashboard is then simply as slow as it used to be.
                    Debug.WriteLine($"Chart warm-up failed: {ex.Message}");
                }
                finally
                {
                    done.TrySetResult();
                }
            });
            return done.Task;
        }
    }

    private static void Warm()
    {
        IsRunning = true;
        try
        {
            DrawMadeUpDashboard();
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static void DrawMadeUpDashboard()
    {
        using var model = DashboardViewModel.CreateWarmUpSample();
        var view = new DashboardView();
        view.Resources.MergedDictionaries.Add(ThemeLoader.Resources);
        view.DataContext = model;
        view.Measure(new Size(1100, 700));
        view.Arrange(new Rect(0, 0, 1100, 700));
        view.UpdateLayout();
        var page = new RenderTargetBitmap(1100, 700, 96, 96, PixelFormats.Pbgra32);
        page.Render(view);
        view.DataContext = null;
    }
}
