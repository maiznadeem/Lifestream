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

    [GeneratedRegex(@"\b(Margin|Padding|CornerRadius|Spacing)=""(?!\{)([^""]*)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex SpacingAttribute();

    [GeneratedRegex(@"<Setter\s+Property=""(Margin|Padding|CornerRadius|Spacing)""\s+Value=""(?!\{)([^""]*)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex SpacingSetter();

    [GeneratedRegex(@"\b(Duration|KeyTime|BeginTime)=""(?!\{)([^""]*)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex MotionAttribute();

    private static bool IsZero(string value) =>
        value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n == 0);

    private static List<string> SpacingLiterals(string file, string text) =>
    [
        .. SpacingAttribute().Matches(text).Where(m => !IsZero(m.Groups[2].Value)).Select(m => $"{file}: {m.Value}"),
        .. SpacingSetter().Matches(text).Where(m => !IsZero(m.Groups[2].Value)).Select(m => $"{file}: {m.Value}"),
    ];

    [Fact]
    public void Theme_NoSpacingRadiusOrMotionLiteralsInViews_OnlyTokens()
    {
        // SHELL-25: every *.xaml outside Theme/, Features/ included, takes spacing, radii and motion from the tokens; 0 is the only literal.
        var themeDir = Path.Combine(DesktopDir, "Theme") + Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        var scanned = new List<string>();
        foreach (var file in ViewXamlFiles().Where(f => !f.StartsWith(themeDir, StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetRelativePath(DesktopDir, file);
            scanned.Add(name);
            offenders.AddRange(SpacingLiterals(name, text));
            offenders.AddRange(MotionAttribute().Matches(text).Select(m => $"{name}: {m.Value}"));
        }

        Assert.True(scanned.Count >= 25, $"expected to scan every view and control, scanned {scanned.Count}");
        Assert.Contains(scanned, f => f.StartsWith("Features" + Path.DirectorySeparatorChar, StringComparison.Ordinal)); // other builders' screens are scanned too
        Assert.True(offenders.Count == 0, $"{offenders.Count} literal(s): " + string.Join("; ", offenders));
    }

    [Fact]
    public void Theme_SpacingScanner_FindsPlantedLiterals_AndAllowsZeroAndTokens()
    {
        Assert.Single(SpacingLiterals("x", "<Border Margin=\"0,16,0,0\" />"));
        Assert.Single(SpacingLiterals("x", "<Border CornerRadius=\"6\" />"));
        Assert.Single(SpacingLiterals("x", "<StackPanel Spacing=\"8\" />"));
        Assert.Single(SpacingLiterals("x", "<Setter Property=\"Padding\" Value=\"12,0\" />"));
        Assert.Empty(SpacingLiterals("x", "<Border Margin=\"0\" Padding=\"0,0,0,0\" CornerRadius=\"{DynamicResource Radius.Card}\" />"));
        Assert.Empty(SpacingLiterals("x", "<Setter Property=\"Margin\" Value=\"{StaticResource Gap.Field}\" />"));
        Assert.Single(MotionAttribute().Matches("<DoubleAnimation Duration=\"0:0:0.2\" />"));
        Assert.Empty(MotionAttribute().Matches("<DoubleAnimation Duration=\"{StaticResource Motion.Fast}\" />"));
    }

    [Fact]
    public void Theme_MotionValuesLiveInTheTokenFile_NotInCode()
    {
        var tokens = File.ReadAllText(Path.Combine(DesktopDir, "Theme", "Tokens.xaml"));
        foreach (var key in new[] { "Motion.Fast", "Motion.Standard", "Motion.Shimmer", "Motion.SpinnerTurn" })
        {
            Assert.Matches("<sys:Double x:Key=\"" + Regex.Escape(key) + @"\.Milliseconds"">\d+</sys:Double>", tokens);
        }

        // MotionSettings only applies the Windows "animation effects" switch; it holds no durations of its own.
        var constants = typeof(MotionSettings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Where(f => f.IsLiteral).Select(f => f.Name);
        Assert.Empty(constants);
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

    /// <summary>Tokens that are neither text nor a surface: scrims, shadows and decoration may fall below the bars.</summary>
    private static readonly string[] ExemptFromContrast = ["Scrim", "Shadow", "BorderSubtle", "SkeletonBase", "SkeletonHighlight", "TextDisabled", "Primary", "PrimaryHover", "PrimaryPressed", "TextOnPrimary"];

    /// <summary>The surfaces text can sit on: every Surface* token and the two danger-button fills, found by name in the token file.</summary>
    private static List<string> SurfaceTokens() => [.. Colours().Keys.Where(k => k.StartsWith("Surface", StringComparison.Ordinal) || k is "DangerHover" or "DangerPressed")];

    /// <summary>Every colour used as text, found by name in the token file: Text* (except the disabled and on-primary ones) and Status*.</summary>
    private static List<string> TextTokens() => [.. Colours().Keys.Where(k => (k.StartsWith("Text", StringComparison.Ordinal) && k is not ("TextDisabled" or "TextOnPrimary")) || k.StartsWith("Status", StringComparison.Ordinal))];

    /// <summary>Every text-on-surface pair (4.5:1), every non-text pair (3:1) and the white-on-primary pairs, computed from the token file rather than listed by hand.</summary>
    public static TheoryData<string, string, double> Pairs()
    {
        var data = new TheoryData<string, string, double>();
        foreach (var (foreground, background, minimum) in ComputePairs())
        {
            data.Add(foreground, background, minimum);
        }

        return data;
    }

    private static List<(string Foreground, string Background, double Minimum)> ComputePairs()
    {
        var data = new List<(string, string, double)>();
        var surfaces = SurfaceTokens();
        foreach (var text in TextTokens())
        {
            foreach (var surface in surfaces)
            {
                data.Add((text, surface, 4.5));
            }
        }

        foreach (var fill in new[] { "Primary", "PrimaryHover", "PrimaryPressed" })
        {
            data.Add(("TextOnPrimary", fill, 4.5));
            data.Add(("FocusRing", fill, 3.0));
        }

        foreach (var nonText in new[] { "FocusRing", "BorderInput" })
        {
            foreach (var surface in surfaces)
            {
                data.Add((nonText, surface, 3.0));
            }
        }

        foreach (var chart in Colours().Keys.Where(k => k.StartsWith("Chart", StringComparison.Ordinal)))
        {
            data.Add((chart, "SurfaceCanvas", 3.0));
            data.Add((chart, "SurfaceRaised", 3.0));
        }

        foreach (var thumb in Colours().Keys.Where(k => k.StartsWith("ScrollThumb", StringComparison.Ordinal)))
        {
            data.Add((thumb, "ScrollTrack", 3.0));
        }

        data.Add(("Primary", "SurfaceCanvas", 3.0));
        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Tokens_TextPairsMeetContrast(string foreground, string background, double minimum)
    {
        var ratio = Contrast(foreground, background);

        Assert.True(ratio >= minimum, $"{foreground} on {background} is {ratio:0.00}:1, needs {minimum}:1");
    }

    [Fact]
    public void Tokens_ContrastPairs_AreEveryTextColourOnEverySurface_NotAHandPickedList()
    {
        var all = Colours().Keys.ToList();
        var texts = TextTokens();
        var surfaces = SurfaceTokens();

        // Discovered by name: a token added to the file joins the pairs without touching this test.
        Assert.Equal(7, surfaces.Count);
        Assert.Contains("SurfacePressed", surfaces);
        Assert.Contains("SurfaceSelected", surfaces);
        Assert.True(texts.Count >= 7, string.Join(",", texts));
        var covered = ComputePairs().Select(p => (p.Foreground, p.Background)).ToHashSet();
        foreach (var text in texts)
        {
            foreach (var surface in surfaces)
            {
                Assert.Contains((text, surface), covered);
            }
        }

        // Every colour token is either in a checked pair or explicitly exempt, so a new one cannot slip through unnoticed.
        var inPairs = covered.SelectMany(c => new[] { c.Item1, c.Item2 }).ToHashSet();
        var unchecked1 = all.Where(k => !inPairs.Contains(k) && !ExemptFromContrast.Contains(k)).ToList();
        Assert.True(unchecked1.Count == 0, "colour tokens neither checked nor exempt: " + string.Join(", ", unchecked1));
    }

    [Fact]
    public void Tokens_MutedTextOnThePressedSurface_MeetsTheBar_Regression()
    {
        // The verifier found muted text on the pressed surface at 4.05:1.
        Assert.True(Contrast("TextMuted", "SurfacePressed") >= 4.5);
        Assert.True(Contrast("TextMuted", "SurfaceSelected") >= 4.5);
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
    public void Typography_FamilyIsTheBundledInter_NoSystemFontIsNamed()
    {
        var text = File.ReadAllText(Path.Combine(DesktopDir, "Theme", "Typography.xaml"));

        Assert.Contains("Fonts/#Inter Variable Text</FontFamily>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Segoe UI Variable", text, StringComparison.Ordinal);
    }

    // Motion

    [Fact]
    public void Motion_Tokens_AreWithinTheDesignedRanges()
    {
        var doc = XDocument.Load(Path.Combine(DesktopDir, "Theme", "Tokens.xaml"));
        TimeSpan Token(string key) => TimeSpan.FromMilliseconds(double.Parse(doc.Descendants().Single(e => e.Name.LocalName == "Double" && (string?)e.Attribute(Xaml + "Key") == key + ".Milliseconds").Value, CultureInfo.InvariantCulture));

        Assert.InRange(Token("Motion.Fast").TotalMilliseconds, 100, 150);
        Assert.InRange(Token("Motion.Standard").TotalMilliseconds, 150, 200);
        Assert.Equal(1200, Token("Motion.Shimmer").TotalMilliseconds);
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

        foreach (var needle in new[] { "surface.canvas", "#17121A", "focus.ring", "Inter", "OFL.txt", "display", "caption", "Skeleton", "Record picker", "Deviation" })
        {
            Assert.Contains(needle, doc, StringComparison.OrdinalIgnoreCase);
        }
    }
}
