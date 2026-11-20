using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lifestream.Client.Tests;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-26: one bundled font family (Google Sans Flex, SIL Open Font License; hinted static instances built by tools/fonts/build-google-sans-flex.py), used by every text style; nothing below 12 DIP.</summary>
[Collection("Wpf")]
public sealed class FontTests
{
    private const string Family = "Google Sans Flex"; // the Win32 family of the static files ("Google Sans Flex", "Google Sans Flex Medium"...)

    private static string DesktopDir => RepoPaths.Find("src/Lifestream.Desktop");

    [Fact]
    public void TheFontFileAndItsLicence_AreInTheRepo_WithTheVersionAndHashRecorded()
    {
        var folder = Path.Combine(DesktopDir, "Fonts");
        Assert.Contains("SIL Open Font License", File.ReadAllText(Path.Combine(folder, "OFL.txt")), StringComparison.Ordinal);
        var record = File.ReadAllText(Path.Combine(folder, "README.md"));
        string[] files = ["GoogleSansFlex-Regular.ttf", "GoogleSansFlex-Medium.ttf", "GoogleSansFlex-SemiBold.ttf", "GoogleSansFlex-Bold.ttf"];
        Assert.Equal(files.Order(), Directory.GetFiles(folder, "*.ttf").Select(Path.GetFileName).Order());
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, file));
            Assert.Contains(Convert.ToHexString(SHA256.HashData(bytes)), record, StringComparison.OrdinalIgnoreCase);
            Assert.True(HasTrueTypeHinting(bytes), $"{file} has no TrueType hinting (fpgm table): it would render soft at small sizes");
        }

        Assert.Contains("5.000", record, StringComparison.Ordinal);
        Assert.Contains("github.com/google/fonts/tree/fa826bb2a3e26df8c1bee99351fc0d039aeed7d2/ofl/googlesansflex", record, StringComparison.Ordinal);
        Assert.Contains("A9AC770798857D5E8AC593CED42B22E40A7C08CEC98319F92B0ECCC2783A77BA", record, StringComparison.OrdinalIgnoreCase); // the upstream variable font the files are cut from
        Assert.Contains("The Google Sans Flex Authors", File.ReadAllText(Path.Combine(folder, "OFL.txt")), StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "OFL.txt")))), record, StringComparison.OrdinalIgnoreCase); // the licence is the one recorded

        // The hash above is of the file's bytes, so git must hand out the same bytes everywhere: with core.autocrlf=true a text file is checked out
        // with CRLF (a fresh Windows checkout failed this test while a long-lived worktree passed). .gitattributes pins the licence and the binary assets.
        var attributes = File.ReadAllLines(RepoPaths.Find(".gitattributes")).Select(line => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToList();
        Assert.Contains("src/Lifestream.Desktop/Fonts/OFL.txt -text", attributes);
        Assert.Contains("*.ttf binary", attributes);
        Assert.Contains("*.ico binary", attributes);
        Assert.Contains("*.png binary", attributes);

        // It ships inside the executable as a WPF resource, so the app needs no installed font.
        Assert.Contains("Fonts\\GoogleSansFlex-*.ttf", File.ReadAllText(Path.Combine(DesktopDir, "Lifestream.Desktop.csproj")), StringComparison.Ordinal);
        Assert.Contains("Resource Include", File.ReadAllText(Path.Combine(DesktopDir, "Lifestream.Desktop.csproj")), StringComparison.Ordinal);
    }

    /// <summary>True when the font has a TrueType font program (the fpgm table): the hinting that snaps stems to pixels.</summary>
    private static bool HasTrueTypeHinting(byte[] font)
    {
        var tables = (font[4] << 8) | font[5];
        return Enumerable.Range(0, tables).Any(i => System.Text.Encoding.ASCII.GetString(font, 12 + (16 * i), 4) == "fpgm");
    }

    /// <summary>The face name starts with the family (it adds the weight, such as "SemiBold") and the file is the one embedded in Lifestream.exe, not an installed font.</summary>
    private static bool IsBundledFont(string face) =>
        face.StartsWith(Family, StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(face, @"/Lifestream;component/Fonts/googlesansflex-(regular|medium|semibold|bold)\.ttf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static IEnumerable<object> AllKeys(ResourceDictionary dictionary) =>
        dictionary.MergedDictionaries.SelectMany(AllKeys).Concat(dictionary.Keys.Cast<object>());

    private static string Resolved(FrameworkElement element)
    {
        var typeface = new Typeface(
            (FontFamily)element.GetValue(TextBlock.FontFamilyProperty),
            (FontStyle)element.GetValue(TextBlock.FontStyleProperty),
            (FontWeight)element.GetValue(TextBlock.FontWeightProperty),
            (FontStretch)element.GetValue(TextBlock.FontStretchProperty));
        Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), "the typeface did not resolve to a font file");
        return glyphs.Win32FamilyNames.Values.First() + "|" + glyphs.FontUri.AbsolutePath;
    }

    [Fact]
    public void EveryTextStyle_ResolvesToTheBundledFont_AndNoTextIsBelow12()
    {
        var results = StaHost.Run(() =>
        {
            var theme = ThemeLoader.Resources;
            var styles = AllKeys(theme).OfType<string>().Where(k => k.StartsWith("Text.", StringComparison.Ordinal) && k != "Text.Icon" && k != "Text.Base").Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
            var list = new List<(string Style, string Face, double Size)>();
            var stage = new StackPanel();
            var blocks = styles.Select(k => new TextBlock { Text = "Ag", Style = (Style)theme[k] }).ToList();
            blocks.ForEach(b => stage.Children.Add(b));
            using var host = Hosted.Create(Hosted.Stage(stage), 400, 600);
            host.Settle(100);
            for (var i = 0; i < styles.Count; i++)
            {
                list.Add((styles[i], Resolved(blocks[i]), blocks[i].FontSize));
            }

            return list;
        });

        Assert.True(results.Count >= 8, "the type ramp must be styles");
        foreach (var step in new[] { "Hero", "Display", "Title", "Section", "Heading", "Body", "Label", "Caption" })
        {
            Assert.Contains(results, r => r.Style == "Text." + step);
        }

        Assert.All(results, r => Assert.True(IsBundledFont(r.Face), $"{r.Style} resolves to {r.Face}, not the bundled font"));
        Assert.All(results, r => Assert.True(r.Size >= 12, $"{r.Style} is {r.Size} DIP"));
        Assert.Equal(14, results.Single(r => r.Style == "Text.Body").Size);
    }

    [Fact]
    public void EveryTextBearingControl_UsesTheBundledFont_NotTheSystemFont()
    {
        var results = StaHost.Run(() =>
        {
            var controls = new FrameworkElement[]
            {
                new Button { Content = "Ag" },
                new TextBox { Text = "Ag" },
                new PasswordBox(),
                new ComboBox { ItemsSource = new[] { "Ag" } },
                new CheckBox { Content = "Ag" },
                new RadioButton { Content = "Ag" },
                new DataGrid(),
                new Label { Content = "Ag" },
                new MenuItem { Header = "Ag" },
            };
            // In a real window, as the app uses them: the window's own font reaches every control that sets none.
            var stage = new StackPanel();
            foreach (var c in controls)
            {
                stage.Children.Add(c);
            }

            var window = new Lifestream.Desktop.Chrome.ChromeWindow { Content = stage, Width = 400, Height = 600 }; // never shown
            window.ApplyTemplate();
            stage.Measure(new Size(400, 600));
            return controls.Select(c => (c.GetType().Name, Resolved(c), (double)c.GetValue(TextBlock.FontSizeProperty))).ToList();
        });

        Assert.All(results, r => Assert.True(IsBundledFont(r.Item2), $"{r.Item1} resolves to {r.Item2}"));
        Assert.All(results, r => Assert.True(r.Item3 >= 12, $"{r.Item1} is {r.Item3} DIP"));
    }

    [Fact]
    public void FontSample_RendersEveryTypeStepInTheBundledFont()
    {
        var png = StaHost.Run(() =>
        {
            var theme = ThemeLoader.Resources;
            var stage = new StackPanel { Margin = new Thickness(16) };
            foreach (var step in new[] { "Hero", "Display", "Title", "Section", "Heading", "Body", "Label", "Caption" })
            {
                stage.Children.Add(new TextBlock { Style = (Style)theme["Text." + step], Text = $"{step} - Lifestream blood bank 0123456789 Ayse Ozturk" });
            }

            var grid = Hosted.Stage(stage);
            grid.Background = (Brush)theme["Brush.Canvas"];
            using var host = Hosted.Create(grid, 900, 260);
            host.Settle(200);
            return host.Capture("FontSample.png");
        });

        Assert.True(StaHost.CountDistinctColours(png) > 10, "the sample shows anti-aliased text");
    }
}
