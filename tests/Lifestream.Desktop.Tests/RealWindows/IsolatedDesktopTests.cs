using Lifestream.Desktop.Services;

namespace Lifestream.Desktop.Tests.RealWindows;

/// <summary>
/// Real windows only ever on an isolated desktop: the rule that decides it, and that everywhere else the real-window tests skip with a message
/// the release gate's runner counts (tools/test-fast.ps1 counts skips that start with "Real-window tests ").
/// </summary>
public sealed class IsolatedDesktopTests
{
    [Theory]
    [InlineData(null, "Default", "Default", "LIFESTREAM_ISOLATED_DESKTOP is not set")]
    [InlineData("", "Default", "Default", "LIFESTREAM_ISOLATED_DESKTOP is not set")]
    [InlineData("Default", "Default", "Default", "not an isolated LifestreamIso-* desktop")]
    [InlineData("LifestreamIso-0A1B2C", "Default", "Default", "not on 'LifestreamIso-0A1B2C'")]
    [InlineData("LifestreamIso-0A1B2C", "LifestreamIso-FFFFFF", "Default", "not on 'LifestreamIso-0A1B2C'")]
    [InlineData("LifestreamIso-0A1B2C", "LifestreamIso-0A1B2C", "LifestreamIso-0A1B2C", "receives input")]
    public void RealWindowsAreRefused_UnlessOnTheNamedIsolatedDesktop_ThatDoesNotReceiveInput(string? expected, string thread, string? input, string because)
    {
        var why = IsolatedDesktop.Check(expected, thread, input);

        Assert.NotNull(why);
        Assert.Contains(because, why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Default")]
    [InlineData(null)] // the input desktop cannot be opened (locked, a UAC prompt): it is still not ours
    [InlineData("Winlogon")]
    public void OnTheNamedIsolatedDesktop_RealWindowsAreAllowed(string? input)
    {
        Assert.Null(IsolatedDesktop.Check("LifestreamIso-0A1B2C", "LifestreamIso-0A1B2C", input));
    }

    [Fact]
    public void AwayFromTheIsolatedDesktop_TheRealWindowTestsSkip_WithAMessageTheRunnerCounts()
    {
        if (IsolatedDesktop.WhyNot is null)
        {
            // On the isolated desktop the only reasons left are the switch and the display.
            Assert.True(RealWindow.Unavailable is null or RealWindow.DisplayIsOff || RealWindow.Unavailable.StartsWith("Real-window tests are turned off", StringComparison.Ordinal), RealWindow.Unavailable);
            return;
        }

        Assert.NotNull(RealWindow.Unavailable);
        Assert.StartsWith("Real-window tests ", RealWindow.Unavailable, StringComparison.Ordinal);
    }
}
