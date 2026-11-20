using System.Windows;
using System.Windows.Media.Animation;

namespace Lifestream.Desktop.Theme;

/// <summary>
/// The motion tokens (SHELL-27) as <see cref="Duration"/> values. The numbers are tokens in Theme/Tokens.xaml
/// (<c>Motion.*.Milliseconds</c>); this class holds none, it only reads them and applies the Windows "Animation effects"
/// switch: when animations are off every duration is zero and the spinner runs once, so nothing moves. Read by Motion.xaml at load time.
/// </summary>
public static class MotionSettings
{
    [ThreadStatic]
    private static ResourceDictionary? _tokens;

    [ThreadStatic]
    private static bool? _override;

    /// <summary>
    /// Tests set this to simulate the Windows setting; null means "ask Windows". Per thread, like the tokens it feeds: each test builds its UI on a
    /// thread of its own, and one test's switch must not change what another loads beside it (ButtonTests read 0 ms with animations on while a
    /// hosted view of another class turned them off).
    /// </summary>
    public static bool? Override
    {
        get => _override;
        set => _override = value;
    }

    public static bool AnimationsEnabled => Override ?? SystemParameters.ClientAreaAnimation;

    public static Duration Fast => Resolve("Motion.Fast.Milliseconds");

    public static Duration Standard => Resolve("Motion.Standard.Milliseconds");

    public static Duration Page => Resolve("Motion.Page.Milliseconds");

    public static Duration PanelOpen => Resolve("Motion.PanelOpen.Milliseconds");

    public static Duration PanelClose => Resolve("Motion.PanelClose.Milliseconds");

    public static Duration Shimmer => Resolve("Motion.Shimmer.Milliseconds");

    public static Duration SpinnerTurn => Resolve("Motion.SpinnerTurn.Milliseconds");

    /// <summary>How long an idle overlay scrollbar stays before it fades. Not an animation, so it does not drop to zero.</summary>
    public static TimeSpan ScrollIdle => TimeSpan.FromMilliseconds((double)Tokens["Motion.ScrollIdle.Milliseconds"]);

    /// <summary>The spinner turns forever, or sits still (one run of zero length) when animations are off.</summary>
    public static RepeatBehavior SpinnerRepeat => AnimationsEnabled ? RepeatBehavior.Forever : new RepeatBehavior(1);

    private static ResourceDictionary Tokens => _tokens ??= new ResourceDictionary { Source = new Uri("/Lifestream;component/Theme/Tokens.xaml", UriKind.Relative) };

    private static Duration Resolve(string token) =>
        AnimationsEnabled ? new Duration(TimeSpan.FromMilliseconds((double)Tokens[token])) : new Duration(TimeSpan.Zero);
}
