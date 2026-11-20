using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// One typeface, drawn one way: what every render of a screen must keep, whatever the screen, state, size or theme. The check reads what was actually
/// drawn, not what a style asks for: it walks the visual tree (and the content of every open popup) and takes each glyph run of each visual's drawing,
/// so text inside control templates, text boxes and documents is seen, and so is a single character that fell back to another font.
/// <list type="bullet">
/// <item>Every run comes from a font file bundled in the executable. The only exception is the icon font (the caption buttons and a few glyph icons).</item>
/// <item>The bundled runs are one family.</item>
/// <item>Every run is drawn with the same text settings: formatting mode, rendering mode, kerning and hinting mode. A heading set to another mode than
/// the text under it looks like another font (2.1.0 did that: Ideal headings over Display text, and grayscale beside ClearType).</item>
/// </list>
/// </summary>
internal static class TextChecks
{
    /// <summary>The families of <c>Font.Icons</c>: glyph icons, never words.</summary>
    private static readonly string[] IconFamilies = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

    /// <summary>One piece of text as it was drawn.</summary>
    internal sealed record Run(string Text, string Owner, string Family, string File, bool Bundled, bool Icon, double Size, string Settings);

    /// <summary>The text settings an element hands down to everything in it: formatting mode, rendering mode and kerning.</summary>
    public static string Inherited(DependencyObject element) =>
        $"{TextOptions.GetTextFormattingMode(element)}/{TextOptions.GetTextRenderingMode(element)}/kerning {(Typography.GetKerning(element) ? "on" : "off")}";

    /// <summary>Fails the test, naming the render, when a rule is broken.</summary>
    public static void Check(Hosted host, string name)
    {
        ArgumentNullException.ThrowIfNull(host);
        Check(host.Element, name);
    }

    /// <summary>Fails the test, naming the render, when a rule is broken under <paramref name="root"/>.</summary>
    public static void Check(Visual root, string name)
    {
        var problems = Problems(Runs(root));
        Assert.True(problems.Count == 0, $"{name}: {string.Join(" | ", problems)}");
    }

    /// <summary>What is wrong with the drawn text, in words; empty when nothing is.</summary>
    public static List<string> Problems(IReadOnlyList<Run> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var problems = new List<string>();
        if (runs.Count == 0)
        {
            problems.Add("no text was found: the walk does not see the render");
            return problems;
        }

        foreach (var foreign in runs.Where(r => !r.Bundled && !r.Icon).GroupBy(r => r.Family).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var first = foreign.First();
            problems.Add($"{foreign.Count()} text(s) drawn in {foreign.Key} ({first.File}), not the bundled font, e.g. \"{first.Text}\" in a {first.Owner}");
        }

        var families = runs.Where(r => r.Bundled).Select(r => r.Family).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (families.Count > 1)
        {
            problems.Add($"the bundled text is {families.Count} families: {string.Join(", ", families)}");
        }

        var settings = runs.GroupBy(r => r.Settings).OrderByDescending(g => g.Count()).ToList();
        if (settings.Count > 1)
        {
            problems.Add($"text is drawn {settings.Count} ways: " + string.Join("; ", settings.Select(g => $"{g.Key} ({g.Count()}, e.g. \"{g.First().Text}\" at {g.First().Size:0.#})")));
        }

        return problems;
    }

    /// <summary>Every glyph run drawn by the visible visuals under the root, the content of open popups included.</summary>
    public static List<Run> Runs(Visual root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var runs = new List<Run>();
        Walk(root, runs);
        return runs;
    }

    private static void Walk(Visual visual, List<Run> runs)
    {
        if (visual is UIElement { IsVisible: false, Visibility: not Visibility.Visible })
        {
            return; // collapsed or hidden: not drawn
        }

        if (VisualTreeHelper.GetDrawing(visual) is { } drawing)
        {
            Collect(drawing, visual, SettingsOwner(visual), runs);
        }

        // A popup's content lives in a tree of its own, reached only through the popup.
        if (visual is Popup { IsOpen: true, Child: Visual content })
        {
            Walk(content, runs);
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
        {
            if (VisualTreeHelper.GetChild(visual, i) is Visual child)
            {
                Walk(child, runs);
            }
        }
    }

    /// <summary>
    /// The element whose text settings a visual's text is drawn with: the visual itself, or, for the bare drawing visuals a text box or a document
    /// draws its lines in (they take no part in property inheritance and only ever report the defaults), the element they belong to.
    /// </summary>
    private static DependencyObject SettingsOwner(Visual visual)
    {
        for (DependencyObject? node = visual; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement)
            {
                return node;
            }
        }

        return visual;
    }

    private static void Collect(Drawing drawing, Visual owner, DependencyObject settings, List<Run> runs)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                foreach (var child in group.Children)
                {
                    Collect(child, owner, settings, runs);
                }

                break;
            case GlyphRunDrawing { GlyphRun: { } run }:
                var text = run.Characters is { Count: > 0 } characters ? new string([.. characters]) : string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    break;
                }

                var face = run.GlyphTypeface;
                var family = face.FamilyNames.Values.FirstOrDefault() ?? "(no name)";
                var file = Uri.UnescapeDataString(face.FontUri.AbsolutePath);
                runs.Add(new Run(
                    text.Length > 32 ? text[..32] : text,
                    owner.GetType().Name,
                    family,
                    Path.GetFileName(file),
                    file.Contains("/Lifestream;component/Fonts/", StringComparison.OrdinalIgnoreCase),
                    IconFamilies.Contains(family, StringComparer.Ordinal),
                    run.FontRenderingEmSize,
                    $"{Inherited(settings)}/hinting {TextOptions.GetTextHintingMode(settings)}"));
                break;
        }
    }
}
