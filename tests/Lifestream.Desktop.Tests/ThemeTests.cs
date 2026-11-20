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

    // C# that builds elements itself must read spacing and radii from the tokens too (SHELL-25), with no exceptions: the printed receipt
    // and the chart series read their sizes from the Print.* and Chart.* tokens.

    [GeneratedRegex(@"//[^\r\n]*|/\*.*?\*/", RegexOptions.CultureInvariant | RegexOptions.Singleline, matchTimeoutMilliseconds: 5000)]
    private static partial Regex CodeComment();

    [GeneratedRegex(@"[$@]*""(?:[^""\\r\n]|\.)*""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex CodeString();

    [GeneratedRegex(@"new\s+(?:Thickness|CornerRadius)\s*\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex SpacingConstructor();

    [GeneratedRegex(@"\b(?:Margin|Padding|PagePadding|CornerRadius)\s*=\s*(?<value>[^;,}\r\n]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex SpacingAssignment();

    [GeneratedRegex(@"(?<![\w.])(?:\d+\.?\d*|\.\d+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex NumberLiteral();

    private static bool HasNonZeroNumber(string expression) =>
        NumberLiteral().Matches(expression).Any(m => double.Parse(m.Value, CultureInfo.InvariantCulture) != 0);

    private static List<string> CodeSpacingLiterals(string file, string text)
    {
        var code = CodeString().Replace(CodeComment().Replace(text, " "), "\"\"");
        var found = new List<string>();
        foreach (var start in SpacingConstructor().Matches(code).Cast<Match>())
        {
            var bodyStart = start.Index + start.Length;
            var depth = 1;
            var end = bodyStart;
            while (end < code.Length && depth > 0)
            {
                depth += code[end] == '(' ? 1 : code[end] == ')' ? -1 : 0;
                end++;
            }

            var body = code[bodyStart..Math.Max(bodyStart, end - 1)];
            if (HasNonZeroNumber(body))
            {
                found.Add($"{file}: {start.Value}{body})");
            }
        }

        found.AddRange(SpacingAssignment().Matches(code).Where(m => !m.Groups["value"].Value.Contains("new ", StringComparison.Ordinal) && HasNonZeroNumber(m.Groups["value"].Value)).Select(m => $"{file}: {m.Value.Trim()}"));
        return found;
    }

    [Fact]
    public void Theme_NoSpacingOrRadiusLiteralsInCSharp_OnlyTokens()
    {
        var offenders = new List<string>();
        var scanned = new List<string>();
        var separator = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(DesktopDir, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal) && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal)))
        {
            var name = Path.GetRelativePath(DesktopDir, file);
            scanned.Add(name);
            offenders.AddRange(CodeSpacingLiterals(name, File.ReadAllText(file)));
        }

        Assert.Contains(Path.Combine("Controls", "Skeleton.cs"), scanned);
        Assert.Contains(Path.Combine("Controls", "ToastView.xaml.cs"), scanned);
        Assert.Contains(Path.Combine("Controls", "RecordPicker.xaml.cs"), scanned);
        Assert.True(offenders.Count == 0, $"{offenders.Count} literal(s) in C#: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Theme_CSharpSpacingScanner_FindsPlantedLiterals_AndAllowsZeroAndTokens()
    {
        Assert.Single(CodeSpacingLiterals("x", "var a = new Thickness(0, 0, 24, 24);"));
        Assert.Single(CodeSpacingLiterals("x", "var a = new CornerRadius(36);"));
        Assert.Single(CodeSpacingLiterals("x", "b.Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);"));
        Assert.Single(CodeSpacingLiterals("x", "b.Padding = 2;"));
        Assert.Single(CodeSpacingLiterals("x", "var a = new Thickness(0, 0, 0, Token(\"Space.16\", 16));"));
        Assert.Empty(CodeSpacingLiterals("x", "var a = new Thickness(0); var b = new CornerRadius(0, 0, 0, 0); var c = new Thickness(0.0);"));
        Assert.Empty(CodeSpacingLiterals("x", "b.Margin = ThemeTokens.Inset(\"Inset.H12\"); b.CornerRadius = ThemeTokens.Radius(\"Radius.Card\");"));
        Assert.Empty(CodeSpacingLiterals("x", "// new Thickness(8) in a comment\nvar s = \"new Thickness(8)\";"));
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

    // Contrast (direction A: light and dark colour files with the same keys; Tokens.xaml no longer holds colours)

    private static readonly string[] Themes = ["Light", "Dark"];

    private static Dictionary<string, (double R, double G, double B, double A)> Colours(string theme)
    {
        var doc = XDocument.Load(Path.Combine(DesktopDir, "Theme", $"Colors.{theme}.xaml"));
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

    /// <summary>A colour as it is seen on a background: a translucent one is laid over the background first.</summary>
    private static (double R, double G, double B, double A) Over((double R, double G, double B, double A) top, (double R, double G, double B, double A) under) =>
        ((top.R * top.A) + (under.R * (1 - top.A)), (top.G * top.A) + (under.G * (1 - top.A)), (top.B * top.A) + (under.B * (1 - top.A)), 1);

    /// <summary>The contrast of a token on a surface token; <paramref name="under"/> is what lies behind a translucent surface (a Mica stop under the active sidebar item).</summary>
    public static double Contrast(string theme, string foreground, string background, string? under = null)
    {
        var colours = Colours(theme);
        var surface = under is null ? colours[background] : Over(colours[background], colours[under]);
        var l1 = Luminance(Over(colours[foreground], surface));
        var l2 = Luminance(surface);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static readonly string[] InkTokens = ["Ink", "Ink2", "Ink3"];

    /// <summary>The opaque surfaces text sits on: the page, panels, table headers and field fills, hovered rows, selected rows.</summary>
    private static readonly string[] TextSurfaces = ["Canvas", "Layer", "Layer2", "Hover", "Selected"];

    /// <summary>The stops of the Mica fallback gradient: the sidebar's and the title bar's text sits straight on it.</summary>
    private static readonly string[] MicaStops = ["MicaStart", "MicaMid", "MicaEnd"];

    /// <summary>The status colours, found by name in the colour file: every token that has a "...Soft" background partner (Blood, Critical, Success, Warning, Info).</summary>
    private static List<string> StatusTokens(string theme)
    {
        var keys = Colours(theme).Keys.ToHashSet();
        return [.. keys.Where(k => keys.Contains(k + "Soft"))];
    }

    /// <summary>The 2.0 names the colour files keep for screens not rebuilt yet. Each must be its token's colour, so it needs no pairs of its own.</summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["SurfaceCanvas"] = "Canvas",
        ["SurfaceRaised"] = "Layer",
        ["SurfaceHover"] = "Hover",
        ["SurfacePressed"] = "Selected",
        ["SurfaceSelected"] = "Selected",
        ["SurfaceInputHover"] = "Layer2",
        ["SurfaceInputFocus"] = "Layer",
        ["TextPrimary"] = "Ink",
        ["TextSecondary"] = "Ink2",
        ["TextMuted"] = "Ink3",
        ["TextDisabled"] = "Ink3",
        ["TextOnPrimary"] = "OnPrimary",
        ["BorderSubtle"] = "Stroke",
        ["SkeletonBase"] = "Skeleton1",
        ["SkeletonHighlight"] = "Skeleton2",
    };

    /// <summary>Tokens that are neither text nor the only sign of anything, each with the reason it is not held to a ratio.</summary>
    private static readonly Dictionary<string, string> ExemptFromContrast = new()
    {
        ["Stroke"] = "hairline panel edges and row separators",
        ["Stroke2"] = "the resting edge of a control (1.4 to 1.9:1): a field also has its StrokeStrong bottom edge, a button its label",
        ["CriticalStroke"] = "the danger button's edge, around its Critical label",
        ["SuccessStroke"] = "an edge around Success text",
        ["Skeleton1"] = "a loading placeholder",
        ["Skeleton2"] = "the shimmer on a loading placeholder",
        ["Scrim"] = "the veil behind a dialog",
        ["Shadow"] = "the colour of shadows",
        ["ChartGrid"] = "the chart's dashed grid lines, behind labelled values",
        ["CaptionHover"] = "ink at 9 %: a hover tint under ink text and icons",
        ["CaptionPressed"] = "ink at 5 %: a pressed tint under ink text and icons",
        ["Well"] = "ink at 6 %: a track or hover tint under ink text and icons",
    };

    /// <summary>
    /// Every pair that must meet a ratio, in BOTH colour files, computed from the files rather than listed by hand (the 2.0 rule: every text colour
    /// on every surface it can sit on). Text at 4.5:1: the three inks on every text surface and on the Mica stops; every status colour on every text
    /// surface and on its own Soft fill; Critical on the pressed danger fill; the text on the filled buttons; the chart's axis text. Non-text at 3:1:
    /// the focus ring on every surface, the field's strong bottom edge, the selected row's bar, the chart series, the wave on the logo. The translucent
    /// surfaces (the active sidebar item, the title-bar search field) are checked over each Mica stop.
    /// </summary>
    public static TheoryData<string, string, string, string?, double> Pairs()
    {
        var data = new TheoryData<string, string, string, string?, double>();
        foreach (var (theme, foreground, background, under, minimum) in ComputePairs())
        {
            data.Add(theme, foreground, background, under, minimum);
        }

        return data;
    }

    private static List<(string Theme, string Foreground, string Background, string? Under, double Minimum)> ComputePairs()
    {
        var data = new List<(string, string, string, string?, double)>();
        foreach (var theme in Themes)
        {
            foreach (var ink in InkTokens)
            {
                foreach (var surface in TextSurfaces.Concat(MicaStops))
                {
                    data.Add((theme, ink, surface, null, 4.5));
                }
            }

            foreach (var status in StatusTokens(theme))
            {
                foreach (var surface in TextSurfaces)
                {
                    data.Add((theme, status, surface, null, 4.5));
                }

                data.Add((theme, status, status + "Soft", null, 4.5));
            }

            data.Add((theme, "Critical", "CriticalPressed", null, 4.5));
            foreach (var fill in new[] { "Primary", "PrimaryHover", "PrimaryPressed" })
            {
                data.Add((theme, "OnPrimary", fill, null, 4.5));
            }

            data.Add((theme, "OnPrimaryPressed", "PrimaryPressed", null, 4.5));
            data.Add((theme, "OnCloseHover", "CloseHover", null, 4.5));
            data.Add((theme, "ChartText", "Layer", null, 4.5));

            foreach (var surface in TextSurfaces.Concat(MicaStops))
            {
                data.Add((theme, "Focus", surface, null, 3.0));
            }

            data.Add((theme, "StrokeStrong", "Layer", null, 3.0));
            data.Add((theme, "StrokeStrong", "Layer2", null, 3.0));
            data.Add((theme, "SelectedBar", "Selected", null, 3.0));
            data.Add((theme, "OnBlood", "Blood", null, 3.0));
            foreach (var chart in Colours(theme).Keys.Where(k => k.StartsWith("Chart", StringComparison.Ordinal) && k is not ("ChartGrid" or "ChartText")))
            {
                data.Add((theme, chart, "Layer", null, 3.0));
                data.Add((theme, chart, "Canvas", null, 3.0));
            }

            foreach (var stop in MicaStops)
            {
                data.Add((theme, "Ink", "NavActive", stop, 4.5));
                data.Add((theme, "Ink2", "NavActive", stop, 4.5));
                data.Add((theme, "Ink", "LayerOnMica", stop, 4.5));
                data.Add((theme, "Ink3", "LayerOnMica", stop, 4.5));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Tokens_TextPairsMeetContrast(string theme, string foreground, string background, string? under, double minimum)
    {
        var ratio = Contrast(theme, foreground, background, under);

        Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background}{(under is null ? string.Empty : " over " + under)} is {ratio:0.00}:1, needs {minimum}:1");
    }

    [Fact]
    public void Tokens_ContrastPairs_AreEveryTextColourOnEverySurface_NotAHandPickedList()
    {
        // Both colour files define exactly the same keys (ThemeService swaps one for the other).
        Assert.Equal(Colours("Light").Keys.Order(StringComparer.Ordinal), Colours("Dark").Keys.Order(StringComparer.Ordinal));

        var pairs = ComputePairs();
        var covered = pairs.Select(p => (p.Theme, p.Foreground, p.Background)).ToHashSet();
        foreach (var theme in Themes)
        {
            // Discovered by name: a status colour added to the files (with its Soft partner) joins the pairs without touching this test.
            var statuses = StatusTokens(theme);
            Assert.Equal(["Blood", "Critical", "Info", "Success", "Warning"], statuses.Order(StringComparer.Ordinal));
            foreach (var text in InkTokens.Concat(statuses))
            {
                foreach (var surface in TextSurfaces)
                {
                    Assert.Contains((theme, text, surface), covered);
                }
            }

            // Every colour of the file is in a checked pair, is a 2.0 alias of a token that is, or is exempt for a stated reason: a new colour
            // cannot slip through unnoticed.
            var inPairs = pairs.Where(p => p.Theme == theme).SelectMany(p => new[] { p.Foreground, p.Background, p.Under }).Where(k => k is not null).ToHashSet();
            var unclassified = Colours(theme).Keys.Where(k => !inPairs.Contains(k) && !Aliases.ContainsKey(k) && !ExemptFromContrast.ContainsKey(k)).ToList();
            Assert.True(unclassified.Count == 0, $"{theme}: colour tokens neither checked, nor an alias, nor exempt: {string.Join(", ", unclassified)}");
            Assert.All(ExemptFromContrast.Keys, key => Assert.DoesNotContain(key, inPairs)); // nothing is both checked and excused
        }
    }

    [Fact]
    public void Tokens_The20Names_AreTheColoursOfTheirTokens_InBothThemes()
    {
        foreach (var theme in Themes)
        {
            var colours = Colours(theme);
            foreach (var (alias, token) in Aliases)
            {
                Assert.True(colours[alias] == colours[token], $"{theme}: Color.{alias} is not Color.{token}");
            }
        }
    }

    [Fact]
    public void Tokens_MutedTextOnTheSelectedHoverAndCanvasSurfaces_MeetsTheBar_Regression()
    {
        // The 2.0 verifier found muted text on the pressed surface at 4.05:1. In A the muted ink (captions, counts, placeholders) sat at 4.2 to 4.4:1
        // on Selected, Hover and Canvas in the light theme; the token was darkened (#677382 to #616D7C) so it meets 4.5:1 there too.
        foreach (var theme in Themes)
        {
            foreach (var surface in new[] { "Selected", "Hover", "Canvas" })
            {
                foreach (var ink in InkTokens)
                {
                    var ratio = Contrast(theme, ink, surface);
                    Assert.True(ratio >= 4.5, $"{theme}: {ink} on {surface} is {ratio:0.00}:1, needs 4.5:1");
                }
            }
        }
    }

    [Fact]
    public void Tokens_ContrastCalculator_IsCorrect_AndRejectsABadPair()
    {
        // The calculator must see that a hairline edge is below the text bar, and must reproduce published ratios.
        Assert.True(Contrast("Light", "Stroke2", "Layer") < 4.5);
        Assert.True(Contrast("Dark", "Stroke2", "Layer") < 4.5);
        Assert.InRange(Contrast("Light", "Ink", "Layer"), 17.7, 18.0);       // #121820 on white: 17.84
        Assert.InRange(Contrast("Light", "OnPrimary", "Primary"), 15.3, 15.6); // white on #1B2533: 15.45
        Assert.Equal(1.0, Contrast("Light", "Layer", "Layer"), 6);

        // A translucent colour is laid over what is behind it: ink at 9 % on white is a faint grey, nowhere near the ink itself.
        Assert.InRange(Contrast("Light", "CaptionHover", "Layer"), 1.05, 1.35);
        // And a translucent surface over a Mica stop is lighter than the stop in the light theme (white at 80 % over it).
        Assert.True(Contrast("Light", "Ink", "NavActive", "MicaStart") > Contrast("Light", "Ink", "MicaStart"));
    }

    [Fact]
    public void Tokens_EveryDesignTokenIsDefined()
    {
        var keys = new[]
        {
            "Canvas", "Layer", "Layer2", "Stroke", "Stroke2", "StrokeStrong", "Ink", "Ink2", "Ink3", "Hover", "Selected", "SelectedBar",
            "Primary", "PrimaryHover", "PrimaryPressed", "OnPrimary", "Focus", "Blood", "BloodSoft",
            "Critical", "CriticalSoft", "Success", "SuccessSoft", "Warning", "WarningSoft", "Info", "InfoSoft",
            "Skeleton1", "Skeleton2", "Scrim", "Shadow", "Chart1", "Chart2",
        };
        foreach (var theme in Themes)
        {
            var colours = Colours(theme);
            foreach (var key in keys)
            {
                Assert.True(colours.ContainsKey(key), $"token Color.{key} missing from Colors.{theme}.xaml");
            }
        }

        var text = File.ReadAllText(Path.Combine(DesktopDir, "Theme", "Tokens.xaml"));
        foreach (var spacing in new[] { "4", "8", "12", "16", "24", "32", "48" })
        {
            Assert.Contains($"x:Key=\"Space.{spacing}\">{spacing}<", text, StringComparison.Ordinal);
        }

        // Direction A sizes: radius 6 control / 8 panel, controls 34 (compact 28), rows 44, header 38, title bar 44, caption buttons 46 x 44.
        Assert.Contains("Radius.Control\">6<", text, StringComparison.Ordinal);
        Assert.Contains("Radius.Panel\">8<", text, StringComparison.Ordinal);
        Assert.Contains("Height.Control\">34<", text, StringComparison.Ordinal);
        Assert.Contains("Height.Compact\">28<", text, StringComparison.Ordinal);
        Assert.Contains("Height.Row\">44<", text, StringComparison.Ordinal);
        Assert.Contains("Height.HeaderRow\">38<", text, StringComparison.Ordinal);
        Assert.Contains("Height.TitleBar\">44<", text, StringComparison.Ordinal);
        Assert.Contains("Height.CaptionButton\">44<", text, StringComparison.Ordinal);
        Assert.Contains("Width.CaptionButton\">46<", text, StringComparison.Ordinal);
    }

    // Typography

    [Fact]
    public void Typography_HasTheRamp_NoTextBelow12_ExceptKeycaps_BodyIs14_OneFamily()
    {
        var doc = XDocument.Load(Path.Combine(DesktopDir, "Theme", "Typography.xaml"));
        var sizes = doc.Descendants().Where(e => e.Name.LocalName == "Double" && ((string?)e.Attribute(Xaml + "Key"))?.StartsWith("FontSize.", StringComparison.Ordinal) == true)
            .ToDictionary(e => ((string)e.Attribute(Xaml + "Key")!)["FontSize.".Length..], e => double.Parse(e.Value, CultureInfo.InvariantCulture));

        // Direction A ramp: Hero 34, Display 32, Title 28, Section 20, Heading 15, Body 14, Label 13, Caption 12, Kbd 11.
        var ramp = new Dictionary<string, double> { ["Hero"] = 34, ["Display"] = 32, ["Title"] = 28, ["Section"] = 20, ["Heading"] = 15, ["Body"] = 14, ["Label"] = 13, ["Caption"] = 12, ["Kbd"] = 11 };
        foreach (var (step, size) in ramp)
        {
            Assert.True(sizes.ContainsKey(step), $"type step {step} missing");
            Assert.Equal(size, sizes[step]);
        }

        // The one exception to "no text below 12": Kbd (11) is for keycap labels only (Ctrl, K, Esc), which are not reading text. Icon is a glyph size.
        Assert.All(sizes.Where(s => s.Key is not ("Kbd" or "Icon")), s => Assert.True(s.Value >= 12, $"font size {s.Key}={s.Value} is below 12"));

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
    public void Typography_FamilyIsTheBundledFont_NoSystemFontIsNamed()
    {
        var text = File.ReadAllText(Path.Combine(DesktopDir, "Theme", "Typography.xaml"));

        Assert.Contains("Fonts/#Google Sans Flex</FontFamily>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Inter", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Segoe UI Variable", text, StringComparison.Ordinal);
    }

    // Motion

    [Fact]
    public void Motion_Tokens_AreWithinTheDesignedRanges()
    {
        var doc = XDocument.Load(Path.Combine(DesktopDir, "Theme", "Tokens.xaml"));
        TimeSpan Token(string key) => TimeSpan.FromMilliseconds(double.Parse(doc.Descendants().Single(e => e.Name.LocalName == "Double" && (string?)e.Attribute(Xaml + "Key") == key + ".Milliseconds").Value, CultureInfo.InvariantCulture));

        Assert.Equal(100, Token("Motion.Fast").TotalMilliseconds);
        Assert.Equal(120, Token("Motion.Page").TotalMilliseconds);
        Assert.Equal(180, Token("Motion.Standard").TotalMilliseconds);
        Assert.Equal(200, Token("Motion.PanelOpen").TotalMilliseconds);
        Assert.Equal(150, Token("Motion.PanelClose").TotalMilliseconds);
        Assert.Equal(1400, Token("Motion.Shimmer").TotalMilliseconds);
        Assert.Equal(900, Token("Motion.SpinnerTurn").TotalMilliseconds);
        Assert.True(Token("Motion.PanelClose") < Token("Motion.PanelOpen"), "a panel leaves faster than it arrives");
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

        Assert.Equal(TimeSpan.FromMilliseconds(100), on.TimeSpan);
        Assert.Equal(TimeSpan.Zero, off.TimeSpan);
    }

    [Fact]
    public void Design_DocumentExists_AndStatesTheTokensAndComponents()
    {
        var doc = File.ReadAllText(RepoPaths.Find("docs/design-system.md"));

        foreach (var needle in new[] { "Google Sans Flex", "`Canvas`", "Brush.<name>", "#F1F3F6", "#111418", "Layer2", "Ink3", "Focus", "Blood", "OFL.txt", "Display", "Caption", "Skeleton", "Record picker", "LogoMark", "Overlay scrollbars", "Motion.PanelOpen", "ChromeWindow", "Sidebar", "Command palette", "Data grid", "SidePanel" })
        {
            Assert.Contains(needle, doc, StringComparison.OrdinalIgnoreCase);
        }
    }
}
