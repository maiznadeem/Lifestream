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

    /// <summary>Tests set this to simulate the Windows setting; null means "ask Windows".</summary>
    public static bool? Override { get; set; }

    public static bool AnimationsEnabled => Override ?? SystemParameters.ClientAreaAnimation;

    public static Duration Fast => Resolve("Motion.Fast.Milliseconds");

    public static Duration Standard => Resolve("Motion.Standard.Milliseconds");

    public static Duration Shimmer => Resolve("Motion.Shimmer.Milliseconds");

    public static Duration SpinnerTurn => Resolve("Motion.SpinnerTurn.Milliseconds");

    /// <summary>The spinner turns forever, or sits still (one run of zero length) when animations are off.</summary>
    public static RepeatBehavior SpinnerRepeat => AnimationsEnabled ? RepeatBehavior.Forever : new RepeatBehavior(1);

    private static ResourceDictionary Tokens => _tokens ??= new ResourceDictionary { Source = new Uri("/Lifestream;component/Theme/Tokens.xaml", UriKind.Relative) };

    private static Duration Resolve(string token) =>
        AnimationsEnabled ? new Duration(TimeSpan.FromMilliseconds((double)Tokens[token])) : new Duration(TimeSpan.Zero);
}
