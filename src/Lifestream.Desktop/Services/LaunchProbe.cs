using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace Lifestream.Desktop.Services;

/// <summary>
/// The launch-time measurement switch (the performance budget "app launch to login window visible", design 5.5). Started with
/// <c>--perf-launch-pipe &lt;name&gt;</c>, the app shows the sign-in window off the desktop (far outside every monitor, not activated,
/// not in the task bar, so it never appears and never takes focus), writes one line to the named pipe the measuring script created when
/// the window has drawn its first frame (<see cref="UIElement"/> content rendered), and exits. Without the switch nothing changes.
/// </summary>
internal static class LaunchProbe
{
    public const string Switch = "--perf-launch-pipe";

    /// <summary>Far outside any monitor layout, so the window cannot be seen even by accident.</summary>
    internal const double OffDesktop = -20000;

    private static string? _pipeName;

    public static bool Enabled => _pipeName is not null;

    /// <summary>The pipe name given after the switch, or null when the switch is absent.</summary>
    internal static string? ParsePipeName(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], Switch, StringComparison.Ordinal) && args[i + 1].Length > 0)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Reads the switch from the command line; called once, first thing in start-up.</summary>
    public static void Configure(IReadOnlyList<string> args) => _pipeName = ParsePipeName(args);

    /// <summary>Makes the window invisible and non-activating, and reports when its first frame is drawn.</summary>
    public static void Prepare(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = OffDesktop;
        window.Top = OffDesktop;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Topmost = false;
        window.ContentRendered += OnFirstRender;
    }

    private static void OnFirstRender(object? sender, EventArgs e)
    {
        ((Window)sender!).ContentRendered -= OnFirstRender;
        Report(_pipeName!, DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime);
        Application.Current.Shutdown();
    }

    /// <summary>Writes "rendered &lt;milliseconds since process start&gt;" to the named pipe; false when nobody is listening.</summary>
    internal static bool Report(string pipeName, TimeSpan sinceProcessStart)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(5000);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"rendered {sinceProcessStart.TotalMilliseconds:F1}"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false; // the measuring script went away; nothing to report to
        }
    }
}
