using System.Windows;
using System.Windows.Media.Animation;

namespace Lifestream.Desktop.Theme;

/// <summary>
/// The motion tokens (SHELL-27): 100 to 150 ms for hover and press, 150 to 200 ms for drawers and dialogs, a 1.2 s
/// skeleton shimmer. All of them are zero when Windows "Animation effects" is off. Read by Motion.xaml at load time.
/// </summary>
public static class MotionSettings
{
    public const int FastMilliseconds = 120;
    public const int StandardMilliseconds = 180;
    public const int ShimmerMilliseconds = 1200;
    public const int SpinnerMilliseconds = 900;

    /// <summary>Tests set this to simulate the Windows setting; null means "ask Windows".</summary>
    public static bool? Override { get; set; }

    public static bool AnimationsEnabled => Override ?? SystemParameters.ClientAreaAnimation;

    public static Duration Fast => Resolve(FastMilliseconds, AnimationsEnabled);

    public static Duration Standard => Resolve(StandardMilliseconds, AnimationsEnabled);

    public static Duration Shimmer => Resolve(ShimmerMilliseconds, AnimationsEnabled);

    public static Duration SpinnerTurn => Resolve(SpinnerMilliseconds, AnimationsEnabled);

    /// <summary>The spinner turns forever, or sits still (one run of zero length) when animations are off.</summary>
    public static RepeatBehavior SpinnerRepeat => AnimationsEnabled ? RepeatBehavior.Forever : new RepeatBehavior(1);

    public static Duration Resolve(int milliseconds, bool animationsEnabled) =>
        animationsEnabled ? new Duration(TimeSpan.FromMilliseconds(milliseconds)) : new Duration(TimeSpan.Zero);
}
