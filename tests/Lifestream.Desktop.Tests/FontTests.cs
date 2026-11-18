using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lifestream.Client.Tests;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-26: one bundled font family (Inter, SIL Open Font License), used by every text style; nothing below 12 DIP.</summary>
[Collection("Wpf")]
public sealed class FontTests
{
    private const string Family = "Inter Variable Text"; // how WPF names the family of InterVariable.ttf (its optical-size instance)

    private static string DesktopDir => RepoPaths.Find("src/Lifestream.Desktop");

    [Fact]
    public void TheFontFileAndItsLicence_AreInTheRepo_WithTheVersionAndHashRecorded()
    {
        var folder = Path.Combine(DesktopDir, "Fonts");
        var font = Path.Combine(folder, "InterVariable.ttf");
        Assert.True(File.Exists(font), "Fonts/InterVariable.ttf is missing");
        Assert.Contains("SIL Open Font License", File.ReadAllText(Path.Combine(folder, "OFL.txt")), StringComparison.Ordinal);

        var record = File.ReadAllText(Path.Combine(folder, "README.md"));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(font)));
        Assert.Contains(hash, record, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.1", record, StringComparison.Ordinal);
        Assert.Contains("github.com/rsms/inter", record, StringComparison.Ordinal);

        // It ships inside the executable as a WPF resource, so the app needs no installed font.
        Assert.Contains("Fonts\\InterVariable.ttf", File.ReadAllText(Path.Combine(DesktopDir, "Lifestream.Desktop.csproj")), StringComparison.Ordinal);
        Assert.Contains("Resource Include", File.ReadAllText(Path.Combine(DesktopDir, "Lifestream.Desktop.csproj")), StringComparison.Ordinal);
    }

    /// <summary>The face name starts with the family (it adds the weight, such as "SemiBold") and the file is the one embedded in Lifestream.exe, not an installed font.</summary>
    private static bool IsBundledInter(string face) =>
        face.StartsWith(Family, StringComparison.Ordinal) && face.EndsWith("|/Lifestream;component/Fonts/intervariable.ttf", StringComparison.OrdinalIgnoreCase);

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
    public void EveryTextStyle_ResolvesToTheBundledInter_AndNoTextIsBelow12()
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

        Assert.True(results.Count >= 6, "the six type steps must be styles");
        foreach (var step in new[] { "Display", "Title", "Heading", "Body", "Label", "Caption" })
        {
            Assert.Contains(results, r => r.Style == "Text." + step);
        }

        Assert.All(results, r => Assert.True(IsBundledInter(r.Face), $"{r.Style} resolves to {r.Face}, not the bundled Inter"));
        Assert.All(results, r => Assert.True(r.Size >= 12, $"{r.Style} is {r.Size} DIP"));
        Assert.Equal(14, results.Single(r => r.Style == "Text.Body").Size);
    }

    [Fact]
    public void EveryTextBearingControl_UsesTheBundledInter_NotTheSystemFont()
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

        Assert.All(results, r => Assert.True(IsBundledInter(r.Item2), $"{r.Item1} resolves to {r.Item2}"));
        Assert.All(results, r => Assert.True(r.Item3 >= 12, $"{r.Item1} is {r.Item3} DIP"));
    }

    [Fact]
    public void FontSample_RendersEveryTypeStepInInter()
    {
        var png = StaHost.Run(() =>
        {
            var theme = ThemeLoader.Resources;
            var stage = new StackPanel { Margin = new Thickness(16) };
            foreach (var step in new[] { "Display", "Title", "Heading", "Body", "Label", "Caption" })
            {
                stage.Children.Add(new TextBlock { Style = (Style)theme["Text." + step], Text = $"{step} - Lifestream blood bank 0123456789 Ayse Ozturk" });
            }

            var grid = Hosted.Stage(stage);
            grid.Background = (Brush)theme["Brush.SurfaceCanvas"];
            using var host = Hosted.Create(grid, 900, 260);
            host.Settle(200);
            return host.Capture("FontSample.png");
        });

        Assert.True(StaHost.CountDistinctColours(png) > 10, "the sample shows anti-aliased text");
    }
}
