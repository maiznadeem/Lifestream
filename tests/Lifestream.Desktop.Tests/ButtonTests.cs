using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Lifestream.Client.Common;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests;

[Collection("Wpf")]
public sealed class ButtonTests
{
    public static TheoryData<string> Variants() => ["Primary", "Secondary", "Ghost", "Danger", "Icon"];

    private static Button Create(string variant, string label = "Save donor")
    {
        var button = new Button { Content = variant == "Icon" ? new IconText { Text = "" } : label, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(FrameworkElement.StyleProperty, $"Button.{variant}");
        ButtonAssist.SetBusyText(button, "Saving...");
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static Grid Stage(Button button)
    {
        var grid = new Grid { Width = 320, Height = 120 };
        grid.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
        grid.Children.Add(button);
        return grid;
    }

    private static Dictionary<string, byte[]> RenderSixStates(string variant, out double idleWidth, out double busyWidth)
    {
        var states = new Dictionary<string, byte[]>();
        var button = Create(variant);
        using var host = Hosted.Create(Stage(button), 320, 120);
        host.Settle();

        void Snap(string name)
        {
            host.Settle(60);
            states[name] = host.Pixels();
        }

        Snap("idle");
        var idle = button.ActualWidth;

        VisualStateManager.GoToState(button, "MouseOver", false);
        Snap("hover");
        VisualStateManager.GoToState(button, "Pressed", false);
        Snap("pressed");
        VisualStateManager.GoToState(button, "Normal", false);

        FocusAssist.SetShowRing(button, true);
        Snap("focus");
        FocusAssist.SetShowRing(button, false);

        ButtonAssist.SetIsBusy(button, true);
        Snap("busy");
        var busy = button.ActualWidth;
        ButtonAssist.SetIsBusy(button, false);

        button.IsEnabled = false;
        Snap("disabled");
        idleWidth = idle;
        busyWidth = busy;
        return states;
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void Button_SixStates_RenderDistinct(string variant)
    {
        var (states, idleWidth, busyWidth) = StaHost.Run(() =>
        {
            var captured = RenderSixStates(variant, out var idle, out var busy);
            return (captured, idle, busy);
        });

        var names = states.Keys.ToList();
        Assert.Equal(["idle", "hover", "pressed", "focus", "busy", "disabled"], names);
        for (var i = 0; i < names.Count; i++)
        {
            for (var j = i + 1; j < names.Count; j++)
            {
                Assert.True(
                    Hosted.Differences(states[names[i]], states[names[j]]) > 20,
                    $"{variant}: {names[i]} and {names[j]} render the same pixels");
            }
        }

        Assert.Equal(idleWidth, busyWidth, 0.5); // busy keeps its width
    }

    [Fact]
    public void PrimaryButton_SixStates_RenderDistinct()
    {
        // The named proof of the plan: one strip image with all six states, saved as a capture.
        var strip = StaHost.Run(() =>
        {
            var states = RenderSixStates("Primary", out _, out _);
            return states;
        });

        var pairs = strip.Keys.SelectMany(a => strip.Keys.Where(b => string.CompareOrdinal(a, b) < 0).Select(b => (a, b))).ToList();
        Assert.Equal(15, pairs.Count);
        Assert.All(pairs, p => Assert.True(Hosted.Differences(strip[p.a], strip[p.b]) > 20, $"{p.a} vs {p.b}"));

        SaveStrip(strip, "PrimaryButton_SixStates.png");
    }

    private static void SaveStrip(Dictionary<string, byte[]> states, string fileName)
    {
        StaHost.Run(() =>
        {
            const int width = 320;
            const int height = 120;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (_, pixels) in states)
            {
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
                panel.Children.Add(new Image { Source = bitmap, Width = width, Height = height });
            }

            using var host = Hosted.Create(panel, width * states.Count, height, withTheme: false);
            host.Capture(fileName);
            return true;
        });
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void Button_HoverAndPress_DoNotShiftLayout(string variant)
    {
        var sizes = StaHost.Run(() =>
        {
            var button = Create(variant);
            using var host = Hosted.Create(Stage(button), 320, 120);
            host.Settle();
            var result = new List<(double W, double H, double X, double Y)>();
            foreach (var state in new[] { "Normal", "MouseOver", "Pressed", "Normal" })
            {
                VisualStateManager.GoToState(button, state, false);
                host.Settle(40);
                var bounds = Hosted.BoundsOf(button, host.Element);
                result.Add((bounds.Width, bounds.Height, bounds.X, bounds.Y));
            }

            return result;
        });

        Assert.Single(sizes.Distinct());
    }

    [Fact]
    public void Button_MinimumSizes_MeetTheDesign()
    {
        // Direction A (docs/design-system.md 5.1): controls are 34 tall, the Small variants 28; icon buttons are square at the same sizes. Short labels
        // do not make a button shorter than its minimum, and these labels are short enough not to make it taller.
        var sizes = StaHost.Run(() =>
        {
            var result = new Dictionary<string, Size>();
            foreach (var variant in new[] { "Primary", "Secondary", "Ghost", "Danger", "Icon", "Primary.Small", "Secondary.Small", "Danger.Small", "Icon.Small" })
            {
                var button = Create(variant.StartsWith("Icon", StringComparison.Ordinal) ? "Icon" : variant, "OK");
                if (variant == "Icon.Small")
                {
                    button.SetResourceReference(FrameworkElement.StyleProperty, "Button.Icon.Small");
                }

                using var host = Hosted.Create(Stage(button), 320, 120);
                host.Settle();
                result[variant] = new Size(button.ActualWidth, button.ActualHeight);
            }

            return result;
        });

        foreach (var variant in new[] { "Primary", "Secondary", "Ghost", "Danger" })
        {
            Assert.Equal(34, sizes[variant].Height, 0.5);
            Assert.True(sizes[variant].Width >= 34, $"{variant} is {sizes[variant].Width} wide");
        }

        Assert.Equal(34, sizes["Icon"].Width, 0.5);
        Assert.Equal(34, sizes["Icon"].Height, 0.5);

        foreach (var variant in new[] { "Primary.Small", "Secondary.Small", "Danger.Small" })
        {
            Assert.Equal(28, sizes[variant].Height, 0.5);
            Assert.True(sizes[variant].Width >= 28, $"{variant} is {sizes[variant].Width} wide");
        }

        Assert.Equal(28, sizes["Icon.Small"].Width, 0.5);
        Assert.Equal(28, sizes["Icon.Small"].Height, 0.5);
    }

    [Fact]
    public void Button_ThatShowsOnlyAnIcon_IsAsWideAsItsIcon_BusyOrNot_AndGetsTheBusyTextBackWithItsLabel()
    {
        // A list screen's "Add" button folds to its icon in a narrow window (its label is collapsed). The busy layer ("Working...") is measured with the
        // label so a button never changes width when it turns busy; for a button that shows no text that made a 44 DIP button about 115 wide.
        var f = StaHost.Run(() =>
        {
            var label = new TextBlock { Text = "Add donor", VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Icon { Kind = IconKind.Userplus, Size = 16, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(label);
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            button.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary"); // the style's own busy text, "Working..."
            using var host = Hosted.Create(Stage(button), 320, 120);
            host.Settle();
            var busyLabel = (TextBlock)button.Template.FindName("BusyLabel", button);
            var spinner = (FrameworkElement)button.Template.FindName("Spinner", button);
            var padding = button.Padding.Left + button.Padding.Right;

            var iconOnly = button.ActualWidth;
            ButtonAssist.SetIsBusy(button, true);
            host.Settle();
            var iconOnlyBusy = (Width: button.ActualWidth, Label: busyLabel.IsVisible, Spinner: spinner.IsVisible, HasLabel: ButtonAssist.GetHasLabel(button));
            ButtonAssist.SetIsBusy(button, false);

            label.Visibility = Visibility.Visible; // the window got wider
            host.Settle();
            var labelled = button.ActualWidth;
            ButtonAssist.SetIsBusy(button, true);
            host.Settle();
            var labelledBusy = (Width: button.ActualWidth, Label: busyLabel.IsVisible, Text: busyLabel.Text, Spinner: spinner.IsVisible, HasLabel: ButtonAssist.GetHasLabel(button));
            ButtonAssist.SetIsBusy(button, false);

            label.Visibility = Visibility.Collapsed; // and narrow again
            host.Settle();
            return (IconOnly: iconOnly, IconOnlyBusy: iconOnlyBusy, Labelled: labelled, LabelledBusy: labelledBusy, Again: button.ActualWidth, Padding: padding);
        });

        Assert.Equal(16 + f.Padding, f.IconOnly, 0.5); // the icon and the button's padding, nothing else
        Assert.Equal(f.IconOnly, f.IconOnlyBusy.Width, 0.5);
        Assert.False(f.IconOnlyBusy.HasLabel);
        Assert.False(f.IconOnlyBusy.Label, "an icon-only button shows no busy text");
        Assert.True(f.IconOnlyBusy.Spinner, "an icon-only button still shows the spinner while busy");

        Assert.True(f.Labelled > f.IconOnly + 40, $"with its label the button is {f.Labelled} wide");
        Assert.Equal(f.Labelled, f.LabelledBusy.Width, 0.5); // the width does not change when it turns busy
        Assert.True(f.LabelledBusy.HasLabel);
        Assert.True(f.LabelledBusy.Label && f.LabelledBusy.Spinner);
        Assert.Equal("Working...", f.LabelledBusy.Text);

        Assert.Equal(f.IconOnly, f.Again, 0.5);
    }

    [Fact]
    public void Button_Busy_IgnoresClicks_AndShowsTheProgressiveVerbWithASpinner()
    {
        var (clicks, busyVisible, contentVisible, status, text) = StaHost.Run(() =>
        {
            var button = Create("Primary");
            var clickCount = 0;
            button.Click += (_, _) => clickCount++;
            using var host = Hosted.Create(Stage(button), 320, 120);
            host.Settle();
            ButtonAssist.SetIsBusy(button, true);
            host.Settle();
            var busyLayer = (FrameworkElement)button.Template.FindName("BusyLayer", button);
            var content = (FrameworkElement)button.Template.FindName("Content", button);
            var label = (TextBlock)button.Template.FindName("BusyLabel", button);

            // A busy button is not hit-testable and its Click is swallowed.
            var peer = (ButtonAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(button)!;
            var hitTestVisible = button.IsHitTestVisible;
            ((IInvokeProvider)peer).Invoke();
            return (clickCount + (hitTestVisible ? 1000 : 0), busyLayer.IsVisible, content.IsVisible, AutomationProperties.GetItemStatus(button), label.Text);
        });

        Assert.True(clicks is 0 or 1, "a busy button is not hit-testable, so the only way to a click is programmatic invoke");
        Assert.True(busyVisible);
        Assert.False(contentVisible);
        Assert.Equal("Busy", status); // exposed to UI Automation
        Assert.Equal("Saving...", text);
    }

    [Fact]
    public void Button_Busy_WithCommandThatDisablesIt_StillLooksBusyNotDisabled()
    {
        var (enabledWhileRunning, disabledPixels, runningPixels) = StaHost.Run(() =>
        {
            var gate = new TaskCompletionSource();
            var command = new AsyncRelayCommand(_ => gate.Task);
            var button = Create("Primary");
            button.Command = command;
            using var host = Hosted.Create(Stage(button), 320, 120);
            host.Settle();
            var idle = host.Pixels();

            _ = command.ExecuteAsync();
            ButtonAssist.SetIsBusy(button, command.IsRunning);
            host.Settle();
            var running = host.Pixels();
            var enabledWhileRunning = button.IsEnabled;

            ButtonAssist.SetIsBusy(button, false);
            host.Settle();
            var disabled = host.Pixels();
            gate.SetResult();
            return (enabledWhileRunning, Hosted.Differences(running, disabled), Hosted.Differences(running, idle));
        });

        Assert.False(enabledWhileRunning); // the command disabled the button...
        Assert.True(disabledPixels > 20); // ...yet the busy look differs from the disabled look
        Assert.True(runningPixels > 20);
    }

    [Fact]
    public void Button_Transitions_UseMotionTokens_AndAreInstantWhenAnimationsAreOff()
    {
        var (fast, instant) = StaHost.Run(() =>
        {
            MotionSettings.Override = true;
            ThemeLoader.Reset();
            var on = ThemeLoader.Resources;
            var template = (ControlTemplate)on["Button.Template"];
            var stateGroups = VisualStateManager.GetVisualStateGroups((FrameworkElement)template.LoadContent());
            var hover = stateGroups.Cast<VisualStateGroup>().SelectMany(g => g.States.Cast<VisualState>()).First(s => s.Name == "MouseOver");
            var animation = (System.Windows.Media.Animation.DoubleAnimation)hover.Storyboard.Children[0];
            var durationOn = animation.Duration.TimeSpan;

            MotionSettings.Override = false;
            ThemeLoader.Reset();
            var off = ThemeLoader.Resources;
            var templateOff = (ControlTemplate)off["Button.Template"];
            var groupsOff = VisualStateManager.GetVisualStateGroups((FrameworkElement)templateOff.LoadContent());
            var hoverOff = groupsOff.Cast<VisualStateGroup>().SelectMany(g => g.States.Cast<VisualState>()).First(s => s.Name == "MouseOver");
            var durationOff = ((System.Windows.Media.Animation.DoubleAnimation)hoverOff.Storyboard.Children[0]).Duration.TimeSpan;
            MotionSettings.Override = null;
            return (durationOn, durationOff);
        });

        Assert.InRange(fast.TotalMilliseconds, 100, 150);
        Assert.Equal(TimeSpan.Zero, instant);
    }

    [Fact]
    public void Button_Variants_AllExist_AndTheFocusRingIsTwoPixels()
    {
        var (variants, thickness) = StaHost.Run(() =>
        {
            var resources = ThemeLoader.Resources;
            var found = new[] { "Primary", "Secondary", "Ghost", "Danger", "Icon" }.Where(v => resources.Contains($"Button.{v}")).ToList();
            var button = Create("Primary");
            using var host = Hosted.Create(Stage(button), 320, 120);
            host.Settle();
            var ring = (Border)button.Template.FindName("FocusRing", button);
            return (found, ring.BorderThickness.Left);
        });

        Assert.Equal(5, variants.Count);
        Assert.Equal(2, thickness);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Button_FocusRing_OnlyAppearsForKeyboardFocus(bool hasFocus, bool lastInputWasKeyboard, bool expected) =>
        Assert.Equal(expected, FocusAssist.ShouldShowRing(hasFocus, lastInputWasKeyboard));
}
