using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Lifestream.Client.Tests;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// SHELL-26, behavioural inventory (fourth round). The earlier inventories walked the realised views, so text that only exists inside a control's own
/// template or inside a popup that is closed (the date picker's calendar, a context menu, a drop-down, a tooltip) was never inspected. This renders every
/// control TEMPLATE that can host text, standalone and off-screen (a presentation source with no window and no popup ever opened), moves the content of
/// each popup next to it, and walks the retained drawing of the whole tree: every glyph run the control would draw must be the bundled Inter at 12 DIP
/// or more. The control types are all the WPF controls the app uses (found by reading the app's XAML and C#), so a control the app starts to use is
/// inspected without anyone adding it to a list.
/// </summary>
[Collection("Wpf")]
public sealed partial class ControlTemplateTextTests
{
    private const double MinimumSize = 12;

    private static string Faces(GlyphRun run) => string.Join("/", run.GlyphTypeface.Win32FamilyNames.Values.Take(1)) + "|" + run.GlyphTypeface.FontUri.AbsolutePath;

    private static bool IsIconFont(GlyphRun run) => run.GlyphTypeface.Win32FamilyNames.Values.Any(n => n.StartsWith("Segoe Fluent Icons", StringComparison.Ordinal) || n.StartsWith("Segoe MDL2 Assets", StringComparison.Ordinal));

    private static bool IsInter(GlyphRun run) =>
        run.GlyphTypeface.Win32FamilyNames.Values.Any(n => n.StartsWith("Inter", StringComparison.Ordinal))
        && System.Text.RegularExpressions.Regex.IsMatch(run.GlyphTypeface.FontUri.AbsolutePath, @"/Lifestream;component/Fonts/inter(display)?-(regular|medium|semibold|bold)\.ttf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Every glyph run that the visual and everything under it draws (the retained drawing of each visual, recursively).</summary>
    private static IEnumerable<(Visual Owner, GlyphRun Run)> GlyphRuns(Visual root)
    {
        var queue = new Queue<Visual>([root]);
        while (queue.Count > 0)
        {
            var visual = queue.Dequeue();
            if (VisualTreeHelper.GetDrawing(visual) is { } drawing)
            {
                foreach (var run in RunsOf(drawing))
                {
                    yield return (visual, run);
                }
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
            {
                if (VisualTreeHelper.GetChild(visual, i) is Visual child)
                {
                    queue.Enqueue(child);
                }
            }
        }
    }

    private static IEnumerable<GlyphRun> RunsOf(Drawing drawing)
    {
        switch (drawing)
        {
            case GlyphRunDrawing glyphs when glyphs.GlyphRun is not null:
                yield return glyphs.GlyphRun;
                break;
            case DrawingGroup group:
                foreach (var child in group.Children)
                {
                    foreach (var run in RunsOf(child))
                    {
                        yield return run;
                    }
                }

                break;
        }
    }

    /// <summary>The WPF controls the app uses: named in the app's XAML or C#, or the base class of one of the app's own controls.</summary>
    private static IEnumerable<Type> UsedControlTypes()
    {
        var source = string.Join('\n', Directory.EnumerateFiles(RepoPaths.Find("src/Lifestream.Desktop"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        var wpf = typeof(Control).Assembly;
        var baseTypes = typeof(Lifestream.Desktop.App).Assembly.GetTypes().Where(t => typeof(Control).IsAssignableFrom(t)).SelectMany(Ancestors).ToHashSet();
        return wpf.GetTypes()
            .Where(t => t is { IsPublic: true, IsAbstract: false, IsGenericTypeDefinition: false } && typeof(Control).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .Where(t => t.Namespace is "System.Windows.Controls" or "System.Windows.Controls.Primitives")
            .Where(t => !typeof(Window).IsAssignableFrom(t) && !typeof(Page).IsAssignableFrom(t) && t != typeof(Frame))
            .Where(t => baseTypes.Contains(t) || UsedInSource(source, t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal);
    }

    private static IEnumerable<Type> Ancestors(Type type)
    {
        for (var current = type; current is not null && current != typeof(Control); current = current.BaseType)
        {
            yield return current;
        }
    }

    private static bool UsedInSource(string source, string name) =>
        Regex.IsMatch(source, @"(?<![\w.])<" + name + @"[\s>/]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5))
        || Regex.IsMatch(source, @"\bnew\s+(?:System\.Windows\.Controls\.(?:Primitives\.)?)?" + name + @"\s*[({]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5))
        || Regex.IsMatch(source, @"TargetType=""" + name + @"""", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    /// <summary>A control of the type holding sample text everywhere text can go.</summary>
    private static Control Sample(Type type, int variant)
    {
        var control = (Control)Activator.CreateInstance(type)!;
        switch (control)
        {
            case TextBox box:
                box.Text = "Sample text 0123";
                break;
            case PasswordBox:
                break;
            case Calendar calendar:
                calendar.SelectedDate = new DateTime(2026, 11, 19);
                calendar.DisplayDate = new DateTime(2026, 11, 19);
                calendar.BlackoutDates.Add(new CalendarDateRange(new DateTime(2026, 11, 1), new DateTime(2026, 11, 5)));
                calendar.DisplayMode = (CalendarMode)variant; // month, year, decade: each view has its own buttons
                break;
            case DatePicker picker:
                picker.SelectedDate = new DateTime(2026, 11, 19);
                break;
            case ComboBox combo:
                combo.IsEditable = variant == 1;
                foreach (var text in new[] { "Sample one", "Sample two", "Sample three" })
                {
                    combo.Items.Add(text);
                }

                combo.SelectedIndex = 0;
                break;
            case DataGrid grid:
                grid.AutoGenerateColumns = false;
                grid.Columns.Add(new DataGridTextColumn { Header = "Header", Binding = new System.Windows.Data.Binding("Name") });
                grid.ItemsSource = new[] { new { Name = "Sample row one" }, new { Name = "Sample row two" } };
                break;
            case TabControl tabs:
                tabs.Items.Add(new TabItem { Header = "Tab one", Content = "Sample text" });
                tabs.Items.Add(new TabItem { Header = "Tab two", Content = "More text" });
                break;
            case Menu or ContextMenu:
                var menu = (ItemsControl)control;
                menu.Items.Add(new MenuItem { Header = "Sample item" });
                menu.Items.Add(new MenuItem { Header = "Checked item", IsCheckable = true, IsChecked = true });
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "Disabled item", IsEnabled = false });
                break;
            case MenuItem item:
                item.Header = "Sample item";
                item.InputGestureText = "Ctrl+S";
                break;
            case HeaderedItemsControl headered:
                headered.Header = "Sample header";
                headered.Items.Add("Sample child");
                break;
            case ItemsControl items:
                foreach (var text in new[] { "Sample one", "Sample two", "Sample three" })
                {
                    items.Items.Add(text);
                }

                break;
            case HeaderedContentControl headeredContent:
                headeredContent.Header = "Sample header";
                headeredContent.Content = "Sample text 0123";
                break;
            case ContentControl content:
                content.Content = "Sample text 0123";
                break;
        }

        return control;
    }

    /// <summary>Checks every glyph run drawn under the root; the root is rendered by the caller.</summary>
    private static void Check(string name, Visual root, List<string> problems, StringBuilder texts)
    {
        foreach (var (owner, run) in GlyphRuns(root))
        {
            var text = run.Characters is { Count: > 0 } c ? new string([.. c]) : "?";
            texts.Append(text).Append('|');
            if (run.FontRenderingEmSize < MinimumSize)
            {
                problems.Add($"{name} > {owner.GetType().Name} draws text at {run.FontRenderingEmSize:0.##} DIP (minimum {MinimumSize}), e.g. '{text}'");
            }

            if (!IsInter(run) && !IsIconFont(run))
            {
                problems.Add($"{name} > {owner.GetType().Name} draws text in {Faces(run)} (not the bundled Inter), e.g. '{text}'");
            }
        }
    }

    /// <summary>
    /// Renders the control standalone, moves the content of every popup it has (never opened) beside it, renders its context menu and tooltip when it has
    /// them, and checks every drawn glyph run.
    /// </summary>
    private static List<string> Problems(Type type, int variant, StringBuilder texts)
    {
        var problems = new List<string>();
        var control = Sample(type, variant);
        if (control is ContextMenu or ToolTip)
        {
            // A context menu or a tooltip cannot have a parent: it is the root of its own presentation source, with nothing inherited (as a popup's root has nothing from a window).
            using var rootHost = Hosted.Create(control, 400, 300);
            rootHost.Settle(150);
            Check(type.Name, control, problems, texts);
            return problems;
        }

        var panel = new StackPanel { Width = 600 };
        // What the window gives everything under it (the app's font and body size); popups hang under the control they open from and inherit from it.
        panel.SetResourceReference(TextElement.FontFamilyProperty, "Font.Family");
        panel.SetValue(TextElement.FontSizeProperty, 14.0);
        panel.Children.Add(control);
        using var host = Hosted.Create(panel, 640, 900);
        host.Settle(100);

        var roots = new List<(string Name, Visual Root)> { (type.Name, panel) };
        foreach (var popup in Hosted.Descendants(panel).OfType<Popup>().ToList())
        {
            if (popup.Child is { } child)
            {
                popup.Child = null; // never opened: its content is shown in the panel instead, under the same inherited text properties as it has in the popup
                var holder = new Border { Child = child };
                foreach (var property in new[] { TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.FontWeightProperty, TextElement.FontStyleProperty, TextElement.ForegroundProperty })
                {
                    holder.SetValue(property, control.GetValue(property));
                }

                panel.Children.Add(holder);
                roots.Add(($"{type.Name} popup", holder));
            }
        }

        host.Settle(150);
        foreach (var (name, root) in roots)
        {
            Check(name, root, problems, texts);
        }

        if (control.ContextMenu is { } own)
        {
            using var menuHost = Hosted.Create(own, 400, 300);
            menuHost.Settle(150);
            Check($"{type.Name}.ContextMenu", own, problems, texts);
        }

        if (control.ToolTip as ToolTip is { } tip)
        {
            using var tipHost = Hosted.Create(tip, 400, 100);
            tipHost.Settle(150);
            Check($"{type.Name}.ToolTip", tip, problems, texts);
        }

        return problems;
    }

    /// <summary>Parts of another control: they only work with their owner (a CalendarItem is the inside of a Calendar and is rendered there), so they are inspected through it.</summary>
    /// <summary>Controls the app does not use today but that hold a calendar or a drop-down (the standard WPF date picker), so they cannot be added later with another font.</summary>
    private static readonly Type[] AlsoInspected = [typeof(DatePicker)];

    private static readonly HashSet<string> PartsOfOtherControls = ["CalendarItem", "CalendarDayButton", "CalendarButton"];

    /// <summary>The number of views of a control that show different text (the calendar has a month, a year and a decade view; an editable combo box has a text box).</summary>
    private static int Variants(Type type) => type == typeof(Calendar) ? 3 : type == typeof(ComboBox) ? 2 : 1;

    [Fact]
    public void EveryTextHostingControlTheAppUses_RenderedStandalone_DrawsOnlyInterAtTwelveDipOrMore()
    {
        var (types, problems, renderings, seen) = StaHost.Run(() =>
        {
            _ = ThemeLoader.Resources;
            var all = new List<string>();
            var used = UsedControlTypes().Where(t => !PartsOfOtherControls.Contains(t.Name)).Concat(AlsoInspected).Distinct().ToList();
            var runs = 0;
            var seenText = new Dictionary<string, string>();
            foreach (var type in used)
            {
                var texts = new StringBuilder();
                for (var variant = 0; variant < Variants(type); variant++)
                {
                    all.AddRange(Problems(type, variant, texts));
                    runs++;
                }

                seenText[type.Name] = texts.ToString();
            }

            return (used.Select(t => t.Name).ToList(), all, runs, seenText);
        });

        // The walk must really have seen the text it is about, or it proves nothing.
        Assert.Contains("November", seen["Calendar"]); // month view: the header
        Assert.Contains("|M|", seen["Calendar"]); // month view: the day names row (M T W T F S S) is really drawn
        Assert.Contains("Nov", seen["Calendar"]); // year view: the month buttons
        Assert.Contains("2020", seen["Calendar"]); // decade view: the year buttons
        Assert.Contains("Sample item", seen["ContextMenu"]);
        Assert.Contains("Sample text", seen["ToolTip"]);
        Assert.Contains("November", seen["DatePicker"]); // the standard date picker's own calendar
        Assert.Contains("Sample two", seen["ComboBox"]); // the drop-down's items, moved out of the closed popup
        Assert.Contains("Sample row one", seen["DataGrid"]);
        Assert.Contains("Sample text", seen["TextBox"]);
        Assert.Contains("Ctrl+V", seen["TextBox"]); // the right-click menu of a text field (Cut, Copy, Paste)
        Assert.Contains("Cut", seen["TextBox"]);
        Assert.Contains("Ctrl+C", seen["FlowDocumentScrollViewer"]); // the receipt's menu
        Assert.True(types.Count >= 25, $"only {types.Count} control types were found");
        Assert.True(problems.Count == 0, $"({types.Count} control types, {renderings} renderings){Environment.NewLine}" + string.Join(Environment.NewLine, problems.GroupBy(p => p[..p.LastIndexOf(", e.g.", StringComparison.Ordinal)]).Select(g => g.First() + (g.Count() > 1 ? $" [{g.Count()} runs]" : string.Empty))));
    }

    /// <summary>
    /// The verifier's probes (v-12-shell, Probe_DateFieldCalendar_TextFontsAndSizes and Probe_UnstyledContextMenu_UnderTheAppTheme_UsesWhichFont) as tests: the
    /// real DateField's calendar (the content of its closed popup, never opened) and the right-click menu of a text field, under the app theme.
    /// </summary>
    [Fact]
    public void TheDateFieldsCalendar_AndTheTextFieldsRightClickMenu_UseInterAtTwelveDipOrMore()
    {
        var (problems, calendarRuns, menuTexts, calendarTexts) = StaHost.Run(() =>
        {
            var found = new List<string>();
            var texts = new StringBuilder();
            var field = new Lifestream.Desktop.Controls.DateField();
            var popup = (Popup)field.FindName("CalendarPopup");
            var panel = new Grid();
            panel.Children.Add(field);
            using var host = Hosted.Create(panel, 400, 400);
            panel.SetResourceReference(TextElement.FontFamilyProperty, "Font.Family");
            panel.SetValue(TextElement.FontSizeProperty, 14.0);
            host.Settle(150);
            var content = popup.Child;
            popup.Child = null; // the popup is never opened: its content is shown under the field instead
            panel.Children.Add(content);
            host.Settle(300);
            Directory.CreateDirectory(CaptureDirectory);
            File.WriteAllBytes(Path.Combine(CaptureDirectory, "DateFieldCalendar_Themed.png"), host.Capture());
            var before = found.Count;
            Check("DateField calendar", content, found, texts);
            var calendarRuns = GlyphRuns(content).Count();

            var box = new TextBox { Text = "Sample" };
            using var boxHost = Hosted.Create(new Grid { Children = { box } }, 300, 100);
            boxHost.Settle(100);
            var menu = box.ContextMenu;
            var menuTexts = new StringBuilder();
            if (menu is not null)
            {
                using var menuHost = Hosted.Create(menu, 300, 200);
                menuHost.Settle(150);
                Directory.CreateDirectory(CaptureDirectory);
                File.WriteAllBytes(Path.Combine(CaptureDirectory, "TextBoxContextMenu_Themed.png"), menuHost.Capture());
                Check("TextBox right-click menu", menu, found, menuTexts);
            }
            else
            {
                found.Add("A text field has no ContextMenu of the theme: WPF builds its own right-click menu in Segoe UI");
            }

            return (found, calendarRuns, menuTexts.ToString(), texts.ToString());
        });

        Assert.True(calendarRuns > 40, $"the calendar drew only {calendarRuns} glyph runs: the walk is not seeing it");
        Assert.Contains("Paste", menuTexts);
        Assert.Contains("|M|", calendarTexts);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct()));
    }

    private static string CaptureDirectory { get; } = Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "captures");
}
