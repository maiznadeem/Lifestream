using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Lifestream.Client.Auth;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// The picture of the sign-in window's wait card (2.1.9): the server stack for step 1, the database for step 2 and the beating blood drop
/// for step 3, only the one of the current step. Each loops while it is shown, and only while Windows animation effects are on
/// (<see cref="MotionSettings.AnimationsEnabled"/>); with them off the picture stands still.
/// </summary>
public partial class WakeStepArt : UserControl
{
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(WakeStep), typeof(WakeStepArt), new PropertyMetadata(WakeStep.None, (d, _) => ((WakeStepArt)d).Show()));

    public WakeStepArt()
    {
        InitializeComponent();
        Loaded += (_, _) => Show();
        Unloaded += (_, _) => StopAll();
    }

    public WakeStep Step
    {
        get => (WakeStep)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ComponentAutomationPeer(this, AutomationControlType.Image, nameof(WakeStepArt));

    /// <summary>True while the shown picture's loop runs.</summary>
    internal bool IsAnimating { get; private set; }

    private void Show()
    {
        ServerArt.Visibility = Step == WakeStep.StartingServer ? Visibility.Visible : Visibility.Collapsed;
        DatabaseArt.Visibility = Step == WakeStep.WakingDatabase ? Visibility.Visible : Visibility.Collapsed;
        SignInArt.Visibility = Step == WakeStep.SigningIn ? Visibility.Visible : Visibility.Collapsed;
        StopAll();
        if (!IsLoaded || Step == WakeStep.None || !MotionSettings.AnimationsEnabled)
        {
            return;
        }

        switch (Step)
        {
            case WakeStep.StartingServer:
                Blink(Led1, 0);
                Blink(Led2, 0.2);
                Blink(Led3, 0.4);
                break;
            case WakeStep.WakingDatabase:
                Level.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(new DoubleAnimation(0.15, 0.9, Seconds(3.2)) { EasingFunction = new SineEase() }, autoReverse: true));
                Ripple.BeginAnimation(TranslateTransform.XProperty, Loop(new DoubleAnimation(0, -24, Seconds(1.4)), autoReverse: false));
                break;
            case WakeStep.SigningIn:
                var beat = new DoubleAnimationUsingKeyFrames { Duration = Seconds(1.1) };
                foreach (var (scale, at) in new[] { (1.0, 0.0), (1.12, 0.15), (0.97, 0.3), (1.06, 0.45), (1.0, 0.6), (1.0, 1.1) })
                {
                    beat.KeyFrames.Add(new LinearDoubleKeyFrame(scale, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at))));
                }

                Beat.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(beat, autoReverse: false));
                Beat.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(beat.Clone(), autoReverse: false));
                Pulse.BeginAnimation(Shape.StrokeDashOffsetProperty, Loop(new DoubleAnimation(90, 0, Seconds(1.1)), autoReverse: false));
                break;
        }

        IsAnimating = true;
    }

    private static Duration Seconds(double seconds) => new(TimeSpan.FromSeconds(seconds));

    private static T Loop<T>(T animation, bool autoReverse)
        where T : AnimationTimeline
    {
        animation.RepeatBehavior = RepeatBehavior.Forever;
        animation.AutoReverse = autoReverse;
        return animation;
    }

    private static void Blink(UIElement led, double delay)
    {
        var blink = new DoubleAnimationUsingKeyFrames { Duration = Seconds(1.6), BeginTime = TimeSpan.FromSeconds(delay) };
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(0.25, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.45))));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.95))));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(0.25, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6))));
        led.BeginAnimation(OpacityProperty, Loop(blink, autoReverse: false));
    }

    private void StopAll()
    {
        foreach (var led in new UIElement[] { Led1, Led2, Led3 })
        {
            led.BeginAnimation(OpacityProperty, null);
        }

        Level.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Ripple.BeginAnimation(TranslateTransform.XProperty, null);
        Beat.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Beat.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Pulse.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        IsAnimating = false;
    }
}
