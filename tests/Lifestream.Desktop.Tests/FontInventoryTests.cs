using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lifestream.Client.Tests;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// SHELL-26 inventory: every text surface of the app uses the bundled font, including FlowDocuments and the printed receipt. The tests walk every view
/// type of the Desktop assembly, every method that builds a document, and every C# file, so a new surface cannot use another font unseen.
/// </summary>
[Collection("Wpf")]
public sealed partial class FontInventoryTests
{
    private const string Family = "Google Sans Flex"; // the Win32 family of the static files: "Google Sans Flex", "Google Sans Flex Medium"...

    private static bool IsBundledFont(string face) =>
        face.StartsWith(Family, StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(face, @"/Lifestream;component/Fonts/googlesansflex-(regular|medium|semibold|bold)\.ttf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string Resolved(FontFamily family, FontStyle style, FontWeight weight, FontStretch stretch)
    {
        var typeface = new Typeface(family, style, weight, stretch);
        Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), "the typeface did not resolve to a font file");
        return glyphs.Win32FamilyNames.Values.First() + "|" + glyphs.FontUri.AbsolutePath;
    }

    private static string ResolvedOf(DependencyObject element) =>
        Resolved(
            (FontFamily)element.GetValue(TextElement.FontFamilyProperty),
            (FontStyle)element.GetValue(TextElement.FontStyleProperty),
            (FontWeight)element.GetValue(TextElement.FontWeightProperty),
            (FontStretch)element.GetValue(TextElement.FontStretchProperty));

    /// <summary>Every view of the app: a UserControl or Window of the Desktop assembly that can be created with no arguments.</summary>
    private static IEnumerable<Type> ViewTypes() =>
        typeof(Lifestream.Desktop.App).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true, IsGenericTypeDefinition: false } && (typeof(UserControl).IsAssignableFrom(t) || typeof(Window).IsAssignableFrom(t)))
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null);

    [Fact]
    public void EveryViewOfTheApp_RendersItsTextInTheBundledFont()
    {
        var (viewed, problems) = StaHost.Run(() =>
        {
            var problems = new List<string>();
            var viewed = 0;
            foreach (var type in ViewTypes().Where(t => typeof(UserControl).IsAssignableFrom(t)))
            {
                var view = (UserControl)Activator.CreateInstance(type)!;
                var window = new Lifestream.Desktop.Chrome.ChromeWindow { Content = view, Width = 900, Height = 700 }; // never shown; the window's font reaches the view as in the app
                window.ApplyTemplate();
                view.Measure(new Size(900, 700));
                view.Arrange(new Rect(0, 0, 900, 700));
                view.UpdateLayout();
                viewed++;
                foreach (var node in Hosted.Descendants(view).Prepend(view))
                {
                    if (node is not (TextBlock or Control) || node is IconText)
                    {
                        continue; // an icon is a glyph from the icon font, not text
                    }

                    var face = ResolvedOf(node);
                    if (!IsBundledFont(face))
                    {
                        problems.Add($"{type.Name} > {node.GetType().Name} uses {face}");
                    }
                }

                window.Close();
            }

            return (viewed, problems);
        });

        Assert.True(viewed >= 25, $"only {viewed} views were walked: the inventory is not finding the views");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct().Take(20)));
    }

    // ---- documents: the receipt on screen, printed and saved as PDF -------------------------------------------------------

    /// <summary>Every method of the Desktop assembly that builds a document or a drawing for output. A new one must be added here, with a test that its text is the bundled font.</summary>
    private static readonly string[] KnownDocumentBuilders = ["ReceiptDocumentBuilder.Build"];

    [Fact]
    public void EveryDocumentBuilder_IsInventoried()
    {
        var builders = typeof(Lifestream.Desktop.App).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Select(m => (Type: t, Method: m)))
            .Where(x => !x.Method.IsSpecialName && (x.Method.ReturnType == typeof(FlowDocument) || x.Method.ReturnType == typeof(FixedDocument) || typeof(DocumentPaginator).IsAssignableFrom(x.Method.ReturnType)))
            .Select(x => $"{x.Type.Name}.{x.Method.Name}")
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(KnownDocumentBuilders, builders);
    }

    private static IEnumerable<TextElement> AllTextElements(FlowDocument document)
    {
        var queue = new Queue<object>([document]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is TextElement element)
            {
                yield return element;
            }

            foreach (var child in LogicalTreeHelper.GetChildren((DependencyObject)node))
            {
                if (child is DependencyObject next)
                {
                    queue.Enqueue(next);
                }
            }
        }
    }

    [Fact]
    public void TheReceiptDocument_EveryRunBlockAndTableCell_IsSetInTheBundledFont()
    {
        var (count, faces) = StaHost.Run(() =>
        {
            _ = ThemeLoader.Resources;
            var receipt = new Lifestream.Client.Features.Receipts.ReceiptDocument("Donation receipt", "R-1", [new("Donor", "Maria Lopez"), new("Units", "2")], "Handle with care", "Duplicate copy", "Admin");
            var document = Lifestream.Desktop.Features.Receipts.ReceiptDocumentBuilder.Build(receipt);
            var elements = AllTextElements(document).ToList();
            return (elements.Count, elements.Select(e => ResolvedOf(e)).Append(ResolvedOf(document)).Distinct().ToList());
        });

        Assert.True(count > 10, "the walk found no text in the receipt");
        Assert.All(faces, face => Assert.True(IsBundledFont(face), $"the receipt uses {face}"));
    }

    [Fact]
    public void ThePrintedAndSavedReceipt_IsBuiltByTheSameBuilder_NotByAnotherDocument()
    {
        var printer = File.ReadAllText(RepoPaths.Find("src/Lifestream.Desktop/Features/Receipts/WpfReceiptPrinter.cs"));
        var view = File.ReadAllText(RepoPaths.Find("src/Lifestream.Desktop/Features/Receipts/ReceiptPrintView.xaml.cs"));

        Assert.Contains("ReceiptDocumentBuilder.Build(", printer, StringComparison.Ordinal);
        Assert.DoesNotContain("new FlowDocument", printer, StringComparison.Ordinal);
        Assert.Contains("ReceiptDocumentBuilder.Build(", view, StringComparison.Ordinal);
    }

    // ---- rendering: one way of drawing text, set in one place ----------------------------------------------------------------

    /// <summary>The three inherited text settings of an element.</summary>
    private static (TextFormattingMode, TextRenderingMode, bool) TextSettings(DependencyObject element) =>
        (TextOptions.GetTextFormattingMode(element), TextOptions.GetTextRenderingMode(element), Typography.GetKerning(element));

    [Fact]
    public void EveryWindowOfTheApp_AndEveryToolTipAndContextMenu_DrawsItsTextWithTheAppsDefaults()
    {
        var found = StaHost.Run(() =>
        {
            var theme = ThemeLoader.Resources;
            var list = new List<(string Name, (TextFormattingMode, TextRenderingMode, bool) Settings)>();
            foreach (var type in ViewTypes().Where(t => typeof(Window).IsAssignableFrom(t) && t != typeof(Lifestream.Desktop.Chrome.ChromeWindow)))
            {
                var window = (Window)Activator.CreateInstance(type)!; // never shown
                list.Add((type.Name, TextSettings(window)));
                window.Close();
            }

            // The two controls that open in a tree of their own and inherit nothing from a window: styled by the theme, as in the app.
            var tip = new ToolTip { Content = "Sample" };
            tip.Resources.MergedDictionaries.Add(theme);
            tip.ApplyTemplate();
            list.Add(("ToolTip", TextSettings(tip)));
            var menu = new ContextMenu();
            menu.Resources.MergedDictionaries.Add(theme);
            menu.ApplyTemplate();
            list.Add(("ContextMenu", TextSettings(menu)));
            return list;
        });

        Assert.True(found.Count(f => f.Name.EndsWith("Window", StringComparison.Ordinal)) >= 4, $"only {found.Count} windows were found: {string.Join(", ", found.Select(f => f.Name))}");
        var expected = (TextDefaults.Formatting, TextDefaults.Rendering, TextDefaults.Kerning);
        Assert.Equal((TextFormattingMode.Display, TextRenderingMode.Grayscale, false), expected); // the choice itself: Fonts/README.md, "Rendering"
        Assert.All(found, f => Assert.True(f.Settings == expected, $"{f.Name} draws text as {f.Settings}, the app as {expected}: put theme:TextDefaults.Apply=\"True\" on its root"));
    }

    [Fact]
    public void NoViewOrStyle_SetsItsOwnTextRendering()
    {
        // A heading in another formatting mode, or a panel in another rendering mode, than the text beside it reads as another typeface (2.1.0). The settings
        // live in Theme/TextDefaults.cs alone. Chrome/Chrome.xaml still carries the window style's old setters, which every window root overrides (it belongs
        // to another change); LifestreamDataGrid copies a block's own settings to measure it.
        var setter = TextRenderingSetter();
        var offenders = new List<string>();
        var folder = RepoPaths.Find("src/Lifestream.Desktop");
        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative is "Chrome/Chrome.xaml" or "Theme/TextDefaults.cs" or "Controls/LifestreamDataGrid.cs")
            {
                continue;
            }

            foreach (Match match in setter.Matches(File.ReadAllText(file)))
            {
                offenders.Add($"{relative}: {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [GeneratedRegex("""TextOptions\.Text(?:Formatting|Rendering)Mode\s*=|Property="TextOptions\.Text(?:Formatting|Rendering)Mode"|Typography\.Kerning\s*=|Property="Typography\.Kerning"|TextOptions\.SetText(?:Formatting|Rendering)Mode|Typography\.SetKerning""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex TextRenderingSetter();

    // ---- code: no literal font anywhere but the theme and the font setup -----------------------------------------------------

    [Fact]
    public void NoCSharpFile_NamesAFontFamily_ExceptTheThemeFontSetup()
    {
        var literal = LiteralFont();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Find("src/Lifestream.Desktop"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var name = Path.GetFileName(file);
            if (name is "FontSetup.cs" or "ChartFont.cs")
            {
                continue; // the font setup names the system fallback, and says so
            }

            foreach (Match match in literal.Matches(File.ReadAllText(file)))
            {
                offenders.Add($"{name}: {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [GeneratedRegex("""new\s+(?:System\.Windows\.Media\.)?FontFamily\s*\(\s*"|FontFamily\s*=\s*new\s+FontFamily|"(?:Segoe UI|Segoe UI Variable|Arial|Calibri|Consolas|Tahoma|Verdana|Times New Roman)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex LiteralFont();
}
