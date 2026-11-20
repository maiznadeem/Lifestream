using System.IO;

namespace Lifestream.Desktop.Services;

/// <summary>
/// Test-only switch for driving the real app from tests/realapp (tools/realapp-isolated.ps1). With <c>LIFESTREAM_OFFSCREEN=1</c> the app runs
/// only on an isolated desktop (<see cref="IsolatedDesktop"/>: a <c>LifestreamIso-*</c> desktop named by <c>LIFESTREAM_ISOLATED_DESKTOP</c>, never
/// the desktop that receives input): anywhere else it exits at once with code 5, before it creates a window. On 2026-11-20 its earlier form,
/// windows kept off every monitor of the person's own desktop by hooks that refused activation, still took the person's keyboard focus and
/// put windows on the monitor for milliseconds; on a desktop of its own nothing can reach the person, so the windows are left to behave as
/// they do for a user (activation, focus, popups, centring, maximizing) and are drawn in software, which the compositor there captures. They
/// are captured with PrintWindow and driven through UI Automation from another process on the same desktop. With
/// <c>LIFESTREAM_PREFERENCES_PATH</c> the preferences file is read from that path instead of the user's own, so a test never changes the theme
/// or remembered username of the person whose PC it runs on. Without the switch this class does nothing.
/// </summary>
internal static class OffscreenMode
{
    public const string Variable = "LIFESTREAM_OFFSCREEN";

    /// <summary>The exit code when the switch is on but the app is not on an isolated desktop.</summary>
    public const int NotIsolatedExitCode = 5;

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable(Variable) == "1";

    /// <summary>The preferences file to use instead of the user's own, or null.</summary>
    public static string? PreferencesPath => Enabled ? Environment.GetEnvironmentVariable("LIFESTREAM_PREFERENCES_PATH") : null;

    /// <summary>Call first thing in start-up, before any window: refuses to go on anywhere but an isolated desktop.</summary>
    public static void Install()
    {
        if (!Enabled)
        {
            return;
        }

        if (IsolatedDesktop.WhyNot is { } why)
        {
            var refusal = $"{Variable}=1 runs only on an isolated desktop (tools/realapp-isolated.ps1), never on the desktop of the person at the PC: {why}. No window was created.";
            Log("REFUSED " + refusal);
            Console.Error.WriteLine(refusal);
            Environment.Exit(NotIsolatedExitCode);
        }

        IsolatedDesktop.UseSoftwareRendering();
        Log($"isolated desktop '{IsolatedDesktop.ThreadDesktopName()}' (input desktop '{IsolatedDesktop.InputDesktopName() ?? "not available"}'), software rendering");
    }

    private static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("LIFESTREAM_OFFSCREEN_LOG") is not { Length: > 0 } log)
        {
            return;
        }

        try
        {
            File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // the log is a diagnostic; a locked file must not stop the app
        }
    }
}
