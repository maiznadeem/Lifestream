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
/// SHELL-26 inventory: every text surface of the app uses the bundled Inter, including FlowDocuments and the printed receipt. The tests walk every view
/// type of the Desktop assembly, every method that builds a document, and every C# file, so a new surface cannot use another font unseen.
/// </summary>
[Collection("Wpf")]
public sealed partial class FontInventoryTests
{
    private const string Family = "Inter Variable Text";

    private static bool IsBundledInter(string face) =>
        face.StartsWith(Family, StringComparison.Ordinal) && face.EndsWith("|/Lifestream;component/Fonts/intervariable.ttf", StringComparison.OrdinalIgnoreCase);

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
    public void EveryViewOfTheApp_RendersItsTextInTheBundledInter()
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
                    if (!IsBundledInter(face))
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

    /// <summary>Every method of the Desktop assembly that builds a document or a drawing for output. A new one must be added here, with a test that its text is Inter.</summary>
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
    public void TheReceiptDocument_EveryRunBlockAndTableCell_IsSetInTheBundledInter()
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
        Assert.All(faces, face => Assert.True(IsBundledInter(face), $"the receipt uses {face}"));
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
