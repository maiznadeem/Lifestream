using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lifestream.Client.Tests;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests;

[Collection("Wpf")]
public sealed partial class ThemeTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string DesktopDir => RepoPaths.Find("src/Lifestream.Desktop");

    private static IEnumerable<string> ViewXamlFiles() =>
        Directory.EnumerateFiles(DesktopDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex(@"(?<![&\w])#[0-9A-Fa-f]{3,8}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex HexLiteral();

    [Fact]
    public void Theme_NoHexLiteralsInViews()
    {
        var themeDir = Path.Combine(DesktopDir, "Theme") + Path.DirectorySeparatorChar;
        var scanned = 0;
        var offenders = new List<string>();
        foreach (var file in ViewXamlFiles().Where(f => !f.StartsWith(themeDir, StringComparison.OrdinalIgnoreCase)))
        {
            scanned++;
            var text = File.ReadAllText(file);
            offenders.AddRange(HexLiteral().Matches(text).Select(m => $"{Path.GetFileName(file)}: {m.Value}"));
        }

        Assert.True(scanned >= 15, $"expected to scan the view and control XAML, scanned {scanned}");
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));

        // The code behind must not smuggle colours in either.
        foreach (var file in Directory.EnumerateFiles(DesktopDir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Color.FromRgb", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Color.FromArgb", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Theme_HexLiteralScanner_FindsAPlantedLiteral()
    {
        Assert.Single(HexLiteral().Matches("<Border Background=\"#C8102E\" />"));
        Assert.Empty(HexLiteral().Matches("<TextBlock Text=\"&#xE70D;\" />"));
    }

    // Contrast

    private static Dictionary<string, (double R, double G, double B, double A)> Colours()
    {
        var doc = XDocument.Load(Path.Combine(DesktopDir, "Theme", "Tokens.xaml"));
        var result = new Dictionary<string, (double, double, double, double)>();
        foreach (var color in doc.Descendants().Where(e => e.Name.LocalName == "Color"))
        {
            var key = (string?)color.Attribute(Xaml + "Key");
            var text = color.Value.Trim();
            if (key is null || !text.StartsWith('#'))
            {
                continue;
            }

            var hex = text[1..];
            var a = hex.Length == 8 ? int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0 : 1.0;
            var rgb = hex[^6..];
            result[key.Replace("Color.", string.Empty, StringComparison.Ordinal)] = (
                int.Parse(rgb[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0,
                int.Parse(rgb[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0,
                int.Parse(rgb[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0,
                a);
        }

        return result;
    }

    private static double Luminance((double R, double G, double B, double A) c)
    {
        static double Lin(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return (0.2126 * Lin(c.R)) + (0.7152 * Lin(c.G)) + (0.0722 * Lin(c.B));
    }

    public static double Contrast(string foreground, string background)
    {
        var colours = Colours();
        var l1 = Luminance(colours[foreground]);
        var l2 = Luminance(colours[background]);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    /// <summary>Every text-on-surface and non-text pair the components use, with its bar (4.5 text, 3 non-text and large text).</summary>
    public static TheoryData<string, string, double> Pairs() => new()
    {
        { "TextPrimary", "SurfaceCanvas", 4.5 },
        { "TextPrimary", "SurfaceRaised", 4.5 },
        { "TextPrimary", "SurfaceHover", 4.5 },
        { "TextPrimary", "SurfacePressed", 4.5 },
        { "TextPrimary", "SurfaceSelected", 4.5 },
        { "TextSecondary", "SurfaceCanvas", 4.5 },
        { "TextSecondary", "SurfaceRaised", 4.5 },
        { "TextSecondary", "SurfaceHover", 4.5 },
        { "TextMuted", "SurfaceRaised", 4.5 },
        { "TextMuted", "SurfaceCanvas", 4.5 },
        { "TextOnPrimary", "Primary", 4.5 },
        { "TextOnPrimary", "PrimaryHover", 4.5 },
        { "TextOnPrimary", "PrimaryPressed", 4.5 },
        { "StatusDanger", "SurfaceRaised", 4.5 },
        { "StatusDanger", "SurfaceCanvas", 4.5 },
        { "StatusDanger", "DangerHover", 4.5 },
        { "StatusDanger", "DangerPressed", 4.5 },
        { "StatusSuccess", "SurfaceRaised", 4.5 },
        { "StatusWarning", "SurfaceRaised", 4.5 },
        { "StatusInfo", "SurfaceRaised", 4.5 },
        { "StatusInfo", "SurfacePressed", 4.5 },
        { "TextSecondary", "SurfacePressed", 4.5 },
        { "FocusRing", "SurfaceCanvas", 3.0 },
        { "FocusRing", "SurfaceRaised", 3.0 },
        { "FocusRing", "Primary", 3.0 },
        { "BorderInput", "SurfaceRaised", 3.0 },
        { "BorderInput", "SurfaceCanvas", 3.0 },
        { "Primary", "SurfaceCanvas", 3.0 },
        { "StatusSuccess", "SurfaceSelected", 3.0 },
        { "StatusInfo", "SurfaceSelected", 3.0 },
        { "Chart1", "SurfaceRaised", 3.0 },
        { "Chart2", "SurfaceRaised", 3.0 },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Tokens_TextPairsMeetContrast(string foreground, string background, double minimum)
    {
        var ratio = Contrast(foreground, background);

        Assert.True(ratio >= minimum, $"{foreground} on {background} is {ratio:0.00}:1, needs {minimum}:1");
    }

    [Fact]
    public void Tokens_ContrastCalculator_IsCorrect_AndRejectsABadPair()
    {
        // The two decorative or exempt pairs are the ones the design says may fall below the bar; the calculator must see that.
        Assert.True(Contrast("TextDisabled", "SurfaceRaised") < 4.5);
        Assert.InRange(Contrast("TextPrimary", "SurfaceCanvas"), 16.0, 16.5); // the audit's published ratio is 16.21
        Assert.InRange(Contrast("TextOnPrimary", "Primary"), 5.8, 6.0);       // 5.88
    }

    [Fact]
    public void Tokens_EveryDesignTokenIsDefined()
    {
        var colours = Colours();
        foreach (var key in new[] { "SurfaceCanvas", "SurfaceRaised", "TextPrimary", "TextSecondary", "TextMuted", "TextDisabled", "Primary", "PrimaryHover", "PrimaryPressed", "FocusRing", "BorderInput", "StatusDanger", "StatusSuccess", "StatusWarning", "StatusInfo", "SkeletonBase", "SkeletonHighlight", "Chart1", "Chart2" })
        {
            Assert.True(colours.ContainsKey(key), $"token Color.{key} missing");
        }

        var text = File.ReadAllText(Path.Combine(DesktopDir, "Theme", "Tokens.xaml"));
        foreach (var spacing in new[] { "4", "8", "12", "16", "24", "32", "48" })
        {
            Assert.Contains($"x:Key=\"Space.{spacing}\">{spacing}<", text, StringComparison.Ordinal);
        }

        Assert.Contains("Radius.Control\">4<", text, StringComparison.Ordinal);
        Assert.Contains("Radius.Card\">8<", text, StringComparison.Ordinal);
        Assert.Contains("Height.CaptionButton\">32<", text, StringComparison.Ordinal);
        Assert.Contains("Width.CaptionButton\">46<", text, StringComparison.Ordinal);
    }

    // Typography

    [Fact]
    public void Typography_HasSixStepScale_NoTextBelow12_BodyIs14_OneFamily()
    {
        var doc = XDocument.Load(Path.Combine(DesktopDir, "Theme", "Typography.xaml"));
        var sizes = doc.Descendants().Where(e => e.Name.LocalName == "Double" && ((string?)e.Attribute(Xaml + "Key"))?.StartsWith("FontSize.", StringComparison.Ordinal) == true)
            .ToDictionary(e => ((string)e.Attribute(Xaml + "Key")!)["FontSize.".Length..], e => double.Parse(e.Value, CultureInfo.InvariantCulture));

        foreach (var step in new[] { "Display", "Title", "Heading", "Body", "Label", "Caption" })
        {
            Assert.True(sizes.ContainsKey(step), $"type step {step} missing");
        }

        Assert.Equal(14, sizes["Body"]);
        Assert.Equal(12, sizes["Caption"]);
        Assert.All(sizes.Values, v => Assert.True(v >= 12, $"font size {v} is below 12"));

        // No literal font size below 12 and no literal font family other than the theme's, anywhere in the views.
        var literal = FontSizeLiteral();
        var themeFamily = FontFamilyLiteral();
        foreach (var file in ViewXamlFiles())
        {
            var content = File.ReadAllText(file);
            foreach (Match match in literal.Matches(content))
            {
                Assert.True(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) >= 12, $"{Path.GetFileName(file)}: {match.Value}");
            }

            if (!file.EndsWith("Typography.xaml", StringComparison.Ordinal))
            {
                Assert.True(!themeFamily.IsMatch(content), $"{Path.GetFileName(file)} sets a literal FontFamily");
            }
        }
    }

    [GeneratedRegex(@"FontSize=""(\d+(?:\.\d+)?)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex FontSizeLiteral();

    [GeneratedRegex(@"FontFamily=""(?!\{)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex FontFamilyLiteral();

    [Fact]
    public void Typography_FamilyIsSegoeUiVariable_WithSegoeUiFallback()
    {
        var text = File.ReadAllText(Path.Combine(DesktopDir, "Theme", "Typography.xaml"));

        Assert.Contains("Segoe UI Variable Text, Segoe UI", text, StringComparison.Ordinal);
    }

    // Motion

    [Fact]
    public void Motion_Tokens_AreWithinTheDesignedRanges_AndZeroWhenAnimationsAreOff()
    {
        Assert.InRange(MotionSettings.FastMilliseconds, 100, 150);
        Assert.InRange(MotionSettings.StandardMilliseconds, 150, 200);
        Assert.Equal(1200, MotionSettings.ShimmerMilliseconds);

        Assert.Equal(TimeSpan.FromMilliseconds(120), MotionSettings.Resolve(MotionSettings.FastMilliseconds, animationsEnabled: true).TimeSpan);
        Assert.Equal(TimeSpan.Zero, MotionSettings.Resolve(MotionSettings.FastMilliseconds, animationsEnabled: false).TimeSpan);
        Assert.Equal(TimeSpan.Zero, MotionSettings.Resolve(MotionSettings.ShimmerMilliseconds, animationsEnabled: false).TimeSpan);
    }

    [Fact]
    public void Motion_Dictionary_FollowsTheWindowsAnimationSetting()
    {
        var on = StaHost.Run(() =>
        {
            MotionSettings.Override = true;
            ThemeLoader.Reset();
            return (System.Windows.Duration)ThemeLoader.Resources["Motion.Fast"];
        });
        var off = StaHost.Run(() =>
        {
            MotionSettings.Override = false;
            ThemeLoader.Reset();
            return (System.Windows.Duration)ThemeLoader.Resources["Motion.Fast"];
        });
        MotionSettings.Override = null;

        Assert.Equal(TimeSpan.FromMilliseconds(120), on.TimeSpan);
        Assert.Equal(TimeSpan.Zero, off.TimeSpan);
    }

    [Fact]
    public void Design_DocumentExists_AndStatesTheTokensAndComponents()
    {
        var doc = File.ReadAllText(RepoPaths.Find("docs/design-system.md"));

        foreach (var needle in new[] { "surface.canvas", "#17121A", "focus.ring", "Segoe UI Variable", "display", "caption", "Skeleton", "Record picker", "Deviation" })
        {
            Assert.Contains(needle, doc, StringComparison.OrdinalIgnoreCase);
        }
    }
}
