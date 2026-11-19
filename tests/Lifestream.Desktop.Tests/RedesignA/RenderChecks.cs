using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// Layout rules every render of a screen must keep, whatever the screen, state, size or theme. The render tests call <see cref="Check"/> on each state they
/// save, so a rule is checked on all of them and not only where someone thought to look:
/// a column header that sits over right-aligned or centred values is aligned with them (ColumnAssist.HeaderAlignment);
/// everything in an open side panel stays inside the panel's 24 DIP padding, with or without a scroll bar;
/// a button's label lies inside its button, and a toolbar's chips inside the toolbar (a control wider than its place is cut by it);
/// and in a standard or wide window nothing in a table is cut off (a name gets the room it needs before the detail columns share the rest) and no badge is
/// squeezed. A compact window may end a long value in an ellipsis; its full text is then in a tooltip, which other tests check.
/// </summary>
internal static class RenderChecks
{
    /// <summary>Fails the test, naming the render, when a rule is broken.</summary>
    public static void Check(Hosted host, string name)
    {
        var problems = Problems(host, name);
        Assert.True(problems.Count == 0, $"{name}: {string.Join(" | ", problems)}");
    }

    /// <summary>What is wrong with the laid-out page, in words; empty when nothing is.</summary>
    public static List<string> Problems(Hosted host, string name)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(name);
        var problems = new List<string>();
        HeadersOverTheirValues(host, problems);
        PanelPadding(host, problems);
        LabelsInsideTheirButtons(host, problems);
        if (!name.Contains("narrow", StringComparison.Ordinal) && !name.Contains("1024x", StringComparison.Ordinal))
        {
            NothingCutOff(host, problems);
        }

        return problems;
    }

    private static void HeadersOverTheirValues(Hosted host, List<string> problems)
    {
        var headers = host.All<DataGridColumnHeader>().Where(h => h.IsVisible && h.Column is not null).ToList();
        var cells = host.All<DataGridCell>().Where(c => c.IsVisible).ToList();
        foreach (var header in headers)
        {
            var alignment = ColumnAssist.GetHeaderAlignment(header.Column);
            if (alignment is not (HorizontalAlignment.Right or HorizontalAlignment.Center))
            {
                continue;
            }

            var title = Descendants<TextBlock>(header).FirstOrDefault(t => t.IsVisible && t.Text.Length > 0);
            var cell = cells.FirstOrDefault(c => ReferenceEquals(c.Column, header.Column));
            var values = cell is null ? [] : Descendants<TextBlock>(cell).Where(t => t.IsVisible && t.ActualWidth > 0).Select(t => Hosted.BoundsOf(t, host.Element)).ToList();
            if (title is null || values.Count == 0)
            {
                continue; // no rows on show (an empty or loading list)
            }

            var over = Hosted.BoundsOf(title, host.Element);
            if (alignment == HorizontalAlignment.Right)
            {
                var right = values.Max(v => v.Right);
                if (Math.Abs(over.Right - right) > 1)
                {
                    problems.Add($"the \"{title.Text}\" header ends at x {over.Right:0.#}, its right-aligned values at {right:0.#}");
                }
            }
            else
            {
                var centre = (values[0].Left + values[0].Right) / 2;
                if (Math.Abs(((over.Left + over.Right) / 2) - centre) > 1)
                {
                    problems.Add($"the \"{title.Text}\" header is centred on x {(over.Left + over.Right) / 2:0.#}, its values on {centre:0.#}");
                }
            }
        }
    }

    private static void PanelPadding(Hosted host, List<string> problems)
    {
        const double Pad = 24;
        foreach (var panel in host.All<SidePanel>().Where(p => p.IsVisible && p.ActualWidth > 0))
        {
            var edge = Hosted.BoundsOf(panel, host.Element);
            foreach (var element in Descendants<FrameworkElement>(panel))
            {
                if (element is not (TextBlock or Badge or BloodGroupTag or ButtonBase or TextBoxBase) || !element.IsVisible || element.ActualWidth <= 0 || IsInside<ScrollBar>(element, panel))
                {
                    continue;
                }

                var bounds = Hosted.BoundsOf(element, host.Element);
                if (bounds.Right > edge.Right - Pad + 0.5 || bounds.Left < edge.Left + Pad - 0.5)
                {
                    problems.Add($"in the side panel, {Describe(element)} spans x {bounds.Left:0.#} to {bounds.Right:0.#}: outside the panel's {Pad} padding ({edge.Left + Pad:0.#} to {edge.Right - Pad:0.#})");
                }
            }
        }
    }

    private static void LabelsInsideTheirButtons(Hosted host, List<string> problems)
    {
        foreach (var button in host.All<ButtonBase>().Where(b => b.IsVisible && b.ActualWidth > 0 && !IsInside<ScrollBar>(b, null) && !IsInside<DataGridColumnHeader>(b, null)))
        {
            foreach (var label in Descendants<TextBlock>(button).Where(t => t.IsVisible && t.ActualWidth > 0 && t.Text.Length > 0))
            {
                var inButton = label.TransformToAncestor(button).TransformBounds(new Rect(0, 0, label.ActualWidth, label.ActualHeight));
                if (inButton.Left < -0.5 || inButton.Right > button.ActualWidth + 0.5)
                {
                    problems.Add($"the label \"{label.Text}\" spans x {inButton.Left:0.#} to {inButton.Right:0.#} in a button {button.ActualWidth:0.#} wide: it is cut");
                }
            }
        }

        // The chips of a list toolbar lie inside the toolbar: a chip past its edge is cut off by the list column.
        foreach (var toolbar in host.All<Lifestream.Desktop.Features.ListScreens.ListToolbar>().Where(t => t.IsVisible && t.ActualWidth > 0))
        {
            foreach (var chip in Descendants<ButtonBase>(toolbar).Where(b => b.IsVisible && b.ActualWidth > 0))
            {
                var inToolbar = chip.TransformToAncestor(toolbar).TransformBounds(new Rect(0, 0, chip.ActualWidth, chip.ActualHeight));
                if (inToolbar.Right > toolbar.ActualWidth + 0.5)
                {
                    problems.Add($"the toolbar's \"{System.Windows.Automation.AutomationProperties.GetName(chip)}\" ends at x {inToolbar.Right:0.#}, the toolbar at {toolbar.ActualWidth:0.#}: it runs off the list's edge");
                }
            }
        }
    }

    private static void NothingCutOff(Hosted host, List<string> problems)
    {
        foreach (var text in host.All<TextBlock>().Where(t => t.IsVisible && t.ActualWidth > 0 && IsInside<DataGridCell>(t, null) && TruncationTooltip.IsTrimmed(t)))
        {
            problems.Add($"cut off in the table: \"{TruncationTooltip.FullText(text)}\"");
        }

        foreach (var badge in host.All<Badge>().Where(b => b.IsVisible))
        {
            if (Descendants<TextBlock>(badge).Any(t => t.IsVisible && TruncationTooltip.IsTrimmed(t)))
            {
                problems.Add($"a squeezed badge: \"{badge.Text}\"");
            }
        }
    }

    private static string Describe(FrameworkElement element) => element switch
    {
        TextBlock text => $"the text \"{TruncationTooltip.FullText(text)}\"",
        Badge badge => $"the badge \"{badge.Text}\"",
        BloodGroupTag tag => $"the blood group tag \"{tag.Text}\"",
        _ => $"a {element.GetType().Name} ({System.Windows.Automation.AutomationProperties.GetName(element)})",
    };

    private static bool IsInside<T>(DependencyObject element, DependencyObject? stopAt)
        where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null && !ReferenceEquals(node, stopAt); node = VisualTreeHelper.GetParent(node))
        {
            if (node is T)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }
}
