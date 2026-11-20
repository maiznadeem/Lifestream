using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lifestream.Client.Tests;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-26: one bundled font family (Onest, SIL Open Font License; hinted static instances built by tools/fonts/build-onest.py), used by every text style; nothing below 12 DIP.</summary>
[Collection("Wpf")]
public sealed class FontTests
{
    private const string Family = "Onest"; // the Win32 family of the static files ("Onest", "Onest Medium", "Onest SemiBold"...)

    private static string DesktopDir => RepoPaths.Find("src/Lifestream.Desktop");

    [Fact]
    public void TheFontFileAndItsLicence_AreInTheRepo_WithTheVersionAndHashRecorded()
    {
        var folder = Path.Combine(DesktopDir, "Fonts");
        Assert.Contains("SIL Open Font License", File.ReadAllText(Path.Combine(folder, "OFL.txt")), StringComparison.Ordinal);
        var record = File.ReadAllText(Path.Combine(folder, "README.md"));
        string[] files = ["Onest-Regular.ttf", "Onest-Medium.ttf", "Onest-SemiBold.ttf", "Onest-Bold.ttf"];
        Assert.Equal(files.Order(), Directory.GetFiles(folder, "*.ttf").Select(Path.GetFileName).Order());
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, file));
            Assert.Contains(Convert.ToHexString(SHA256.HashData(bytes)), record, StringComparison.OrdinalIgnoreCase);
            Assert.True(HasTrueTypeHinting(bytes), $"{file} has no TrueType hinting (fpgm table): it would render soft at small sizes");
        }

        Assert.Contains("2.001", record, StringComparison.Ordinal);
        Assert.Contains("github.com/google/fonts/tree/4c1db3aec83c67dd223dd82a68c039cab30917a9/ofl/onest", record, StringComparison.Ordinal);
        Assert.Contains("Onest Project Authors", File.ReadAllText(Path.Combine(folder, "OFL.txt")), StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "OFL.txt")))), record, StringComparison.OrdinalIgnoreCase); // the licence is the one recorded

        // The hash above is of the file's bytes, so git must hand out the same bytes everywhere: with core.autocrlf=true a text file is checked out
        // with CRLF (a fresh Windows checkout failed this test while a long-lived worktree passed). .gitattributes pins the licence and the binary assets.
        var attributes = File.ReadAllLines(RepoPaths.Find(".gitattributes")).Select(line => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToList();
        Assert.Contains("src/Lifestream.Desktop/Fonts/OFL.txt -text", attributes);
        Assert.Contains("*.ttf binary", attributes);
        Assert.Contains("*.ico binary", attributes);
        Assert.Contains("*.png binary", attributes);

        // It ships inside the executable as a WPF resource, so the app needs no installed font.
        Assert.Contains("Fonts\\Onest-*.ttf", File.ReadAllText(Path.Combine(DesktopDir, "Lifestream.Desktop.csproj")), StringComparison.Ordinal);
        Assert.Contains("Resource Include", File.ReadAllText(Path.Combine(DesktopDir, "Lifestream.Desktop.csproj")), StringComparison.Ordinal);
    }

    /// <summary>True when the font has a TrueType font program (the fpgm table): the hinting that snaps stems to pixels.</summary>
    private static bool HasTrueTypeHinting(byte[] font)
    {
        var tables = (font[4] << 8) | font[5];
        return Enumerable.Range(0, tables).Any(i => System.Text.Encoding.ASCII.GetString(font, 12 + (16 * i), 4) == "fpgm");
    }

    /// <summary>The face name starts with the family (it adds the weight, such as "SemiBold") and the file is the one embedded in Lifestream.exe, not an installed font.</summary>
    private static bool IsBundledOnest(string face) =>
        face.StartsWith(Family, StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(face, @"/Lifestream;component/Fonts/onest-(regular|medium|semibold|bold)\.ttf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
    public void EveryTextStyle_ResolvesToTheBundledOnest_AndNoTextIsBelow12()
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

        Assert.All(results, r => Assert.True(IsBundledOnest(r.Face), $"{r.Style} resolves to {r.Face}, not the bundled Onest"));
        Assert.All(results, r => Assert.True(r.Size >= 12, $"{r.Style} is {r.Size} DIP"));
        Assert.Equal(14, results.Single(r => r.Style == "Text.Body").Size);
    }

    [Fact]
    public void EveryTextBearingControl_UsesTheBundledOnest_NotTheSystemFont()
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

        Assert.All(results, r => Assert.True(IsBundledOnest(r.Item2), $"{r.Item1} resolves to {r.Item2}"));
        Assert.All(results, r => Assert.True(r.Item3 >= 12, $"{r.Item1} is {r.Item3} DIP"));
    }

    [Fact]
    public void FontSample_RendersEveryTypeStepInOnest()
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
