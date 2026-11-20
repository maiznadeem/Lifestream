namespace Lifestream.Desktop.Tests.RealWindows;

/// <summary>
/// The real-window tests cannot see anything while the display is off: the compositor draws nothing then, and each of them used to wait 60 s for
/// a drawn window and fail. They now ask Windows for the state of the display first and skip at once with one fixed message, which the test
/// runner of the release gate counts (a gate must not lose those checks without saying so). These tests hold that message and the rule, and that
/// the question to Windows comes back by itself: it needs no window and sends no input.
/// </summary>
public sealed class RealWindowDisplayTests
{
    [Fact]
    public void OnlyADisplayThatIsOff_SkipsTheRealWindowChecks_WithTheMessageTheRunnerCounts()
    {
        Assert.Equal("display is off: real-window checks not run", RealWindow.DisplayIsOff);
        Assert.Equal(RealWindow.DisplayIsOff, RealWindow.DisplayReason(0)); // off
        Assert.Null(RealWindow.DisplayReason(1)); // on
        Assert.Null(RealWindow.DisplayReason(2)); // dimmed: still drawn
        Assert.Null(RealWindow.DisplayReason(null)); // Windows did not say: the tests run and speak for themselves
    }

    [Fact]
    public void WindowsIsAskedForTheDisplayState_AndTheWaitForItsAnswerIsWellUnderASecond()
    {
        var state = RealWindow.ConsoleDisplayState();

        Assert.True(state is null or 0 or 1 or 2, $"the display state came back as {state}");
        Assert.InRange(RealWindow.DisplayStateWaitMilliseconds, 1, 999);
    }
}
