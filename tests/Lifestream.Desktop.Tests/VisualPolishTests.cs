using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lifestream.Client.Auth;
using Lifestream.Client.Tests;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Chrome;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// The 2.0.1 visual refresh: the app icon is real and embedded in the executable, Button and TextBox carry the shared corner radius, hover and
/// focus treatment, and the text of a rendered sign-in screen is drawn in the bundled Inter (not a fallback) with crisp small-text layout.
/// </summary>
[Collection("Wpf")]
public sealed class VisualPolishTests
{
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    /// <summary>The folder whose Lifestream.exe is checked: <c>LIFESTREAM_PUBLISH_DIR</c> (a <c>dotnet publish</c> output, as release.yml makes it) when set, else the build output.</summary>
    private static string ExeFolder() =>
        Environment.GetEnvironmentVariable("LIFESTREAM_PUBLISH_DIR") is { Length: > 0 } published
            ? published
            : Directory.GetDirectories(RepoPaths.Find("src/Lifestream.Desktop/bin"), "*", SearchOption.AllDirectories)
                .Where(d => File.Exists(Path.Combine(d, "Lifestream.exe")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "Lifestream.exe")))
                .First();

    [Fact]
    public void Icon_TheIcoHasEverySize_AndTheExecutableEmbedsItAsItsIcon()
    {
        var ico = File.ReadAllBytes(RepoPaths.Find("src/Lifestream.Desktop/Assets/Lifestream.ico"));
        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2)); // type 1: icon
        var count = BitConverter.ToUInt16(ico, 4);
        var sizes = Enumerable.Range(0, count).Select(i => ico[6 + (16 * i)] == 0 ? 256 : ico[6 + (16 * i)]).Order().ToArray();
        Assert.Equal(IconSizes, sizes);

        var exe = Path.Combine(ExeFolder(), "Lifestream.exe");
        Assert.True(File.Exists(exe), exe);
        var image = File.ReadAllBytes(exe);
        for (var i = 0; i < count; i++)
        {
            // Each RT_ICON resource holds the frame's bytes exactly as they are in the .ico; without them Windows shows a generic icon in the Start menu and taskbar.
            var length = BitConverter.ToInt32(ico, 6 + (16 * i) + 8);
            var offset = BitConverter.ToInt32(ico, 6 + (16 * i) + 12);
            Assert.True(image.AsSpan().IndexOf(ico.AsSpan(offset, length)) >= 0, $"the {(ico[6 + (16 * i)] == 0 ? 256 : ico[6 + (16 * i)])} px frame is not embedded in {exe}");
        }

        // Setup.exe gets the same icon from vpk pack.
        Assert.Contains("'--icon', 'src/Lifestream.Desktop/Assets/Lifestream.ico'", File.ReadAllText(RepoPaths.Find(".github/workflows/release.yml")), StringComparison.Ordinal);
    }

    [Fact]
    public void Theme_ButtonAndTextBox_GetTheSharedRadius_HoverAndFocusTreatment()
    {
        var facts = StaHost.Run(() =>
        {
            var panel = new StackPanel { Width = 300 };
            var button = new Button { Content = "Save" };
            button.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
            var box = new TextBox();
            InputAssist.SetPlaceholder(box, "Search donors");
            panel.Children.Add(button);
            panel.Children.Add(box);
            using var host = Hosted.Create(panel, 300, 120);
            host.Settle(100);

            var control = (CornerRadius)host.Element.FindResource("Radius.Control");
            var rest = (Border)button.Template.FindName("Rest", button);
            var hover = (Border)button.Template.FindName("HoverLayer", button);
            var ring = (Border)button.Template.FindName("FocusRing", button);
            var hoverBefore = hover.Opacity;
            VisualStateManager.GoToState(button, "MouseOver", useTransitions: true);
            host.Settle(300);
            var hoverAfter = hover.Opacity;
            FocusAssist.SetShowRing(button, true);
            host.Settle(50);

            var boxBorder = (Border)box.Template.FindName("Box", box);
            var underline = (Border)box.Template.FindName("Underline", box);
            var hoverFill = (Border)box.Template.FindName("HoverFill", box);
            var placeholder = (TextBlock)box.Template.FindName("Placeholder", box);
            var placeholderShown = placeholder.IsVisible;
            box.Focus();
            host.Settle(300);
            var underlineFocused = underline.Opacity;
            var focusedBorder = ((SolidColorBrush)boxBorder.BorderBrush).Color;
            box.Text = "Ayşe";
            host.Settle(50);

            return (control, rest.CornerRadius, ButtonAssist.GetHoverBackground(button) is not null, hoverBefore, hoverAfter, ring.IsVisible, ring.CornerRadius,
                boxBorder.CornerRadius, hoverFill.Background is not null, placeholderShown, placeholder.Text, placeholder.IsVisible, box.IsKeyboardFocusWithin, underlineFocused,
                focusedBorder, (Color)host.Element.FindResource("Color.FocusRing"), (CornerRadius)host.Element.FindResource("Radius.Card"));
        });

        Assert.Equal(new CornerRadius(8), facts.control);
        Assert.Equal(facts.control, facts.Item2);              // button corners
        Assert.True(facts.Item3, "the button has a hover fill");
        Assert.Equal(0, facts.hoverBefore);
        Assert.Equal(1, facts.hoverAfter, 2);                  // the hover state fades the fill in
        Assert.True(facts.Item6, "the keyboard focus ring shows");
        Assert.Equal(new CornerRadius(12), facts.Item7);       // the ring follows the corner 4 DIP out
        Assert.Equal(facts.control, facts.Item8);              // text box corners
        Assert.True(facts.Item9, "the text box has a hover fill");
        Assert.True(facts.placeholderShown, "an empty field shows its placeholder");
        Assert.Equal("Search donors", facts.Item11);
        Assert.False(facts.Item12, "the placeholder goes away once there is text");
        if (facts.Item13)
        {
            Assert.Equal(1, facts.underlineFocused, 2);        // the accent line under a focused field
            Assert.Equal(facts.Item16, facts.focusedBorder);
        }

        Assert.Equal(new CornerRadius(12), facts.Item17);      // cards and dialogs
    }

    [Fact]
    public void Font_EveryTextOfARenderedSignInScreen_IsTheBundledInter_WithRealWeights_AndWindowsUseDisplayFormatting()
    {
        var (texts, mode) = StaHost.Run(() =>
        {
            using var rig = new Rig(dispatcher: WpfTestUi.Dispatcher());
            using var host = Hosted.Create(new LoginView { DataContext = rig.Get<LoginViewModel>() }, 1040, 668);
            host.Settle(300);
            var icons = (FontFamily)host.Element.FindResource("Font.Icons");
            var found = host.All<TextBlock>()
                .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && t.FontFamily.Source != icons.Source)
                .Select(t =>
                {
                    var resolved = new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch).TryGetGlyphTypeface(out var glyphs);
                    return (t.Text, Resolved: resolved, Family: resolved ? string.Join("|", glyphs!.FamilyNames.Values) : string.Empty, Uri: resolved ? glyphs!.FontUri.ToString() : string.Empty,
                        Requested: t.FontWeight, Weight: resolved ? glyphs!.Weight : default, Simulated: resolved ? glyphs!.StyleSimulations : StyleSimulations.None,
                        Missing: resolved ? t.Text.Count(c => !char.IsWhiteSpace(c) && !glyphs!.CharacterToGlyphMap.ContainsKey(c)) : t.Text.Length);
                })
                .ToList();
            var window = new LoginWindow();
            var display = TextOptions.GetTextFormattingMode(window);
            window.Close();
            return (found, display);
        });

        Assert.True(texts.Count >= 8, $"only {texts.Count} texts rendered");
        Assert.Contains(texts, t => t.Text == "Sign in");
        Assert.Contains(texts, t => t.Requested == FontWeights.Bold);
        Assert.All(texts, t =>
        {
            Assert.True(t.Resolved, $"'{t.Text}' did not resolve to a font");
            Assert.StartsWith("Inter", t.Family, StringComparison.Ordinal);
            Assert.Matches(@"(?i)/Lifestream;component/Fonts/inter(display)?-(regular|medium|semibold|bold)\.ttf$", t.Uri);
            Assert.Equal(t.Requested, t.Weight);                // a real named instance of the weight...
            Assert.Equal(StyleSimulations.None, t.Simulated);  // ...not a synthesised bold
            Assert.Equal(0, t.Missing);                          // and every character is in Inter: no per-glyph fallback
        });
        Assert.Equal(TextFormattingMode.Display, mode);
    }

    [Fact]
    public void Window_EveryChromeWindow_ShowsTheAppIcon()
    {
        var icon = StaHost.Run(() =>
        {
            var window = new ChromeWindow();
            var source = window.Icon?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            window.Close();
            return source;
        });

        Assert.Equal("pack://application:,,,/Lifestream;component/Assets/Lifestream.ico", icon);
    }
}
