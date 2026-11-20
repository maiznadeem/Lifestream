using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Lifestream.Client.Common;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.ListScreens;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// The shared pieces finished after the fix round, each on its own (the render tests check the same rules on every screen through
/// <see cref="RenderChecks"/>): the load surface puts its empty and error states at the top; a side panel's content keeps its 24 DIP right padding with or
/// without a scroll bar; a badge given too little room ends in an ellipsis with its full text as a tooltip; and a list's name column gets the width its
/// names need, with the lowest-priority column folding away before a name is cut.
/// </summary>
[Collection("Wpf")]
public sealed class SharedLeftoverTests
{
    private sealed record Person(string Name, string Group, string Detail, string Extra);

    [Theory]
    [InlineData(LoadState.Empty, false)]
    [InlineData(LoadState.Empty, true)]
    [InlineData(LoadState.Error, false)]
    [InlineData(LoadState.Error, true)]
    public void LoadSurface_EmptyAndError_StartAtTheTop_AndNothingHiddenTakesRoom(LoadState state, bool fillsThePage)
    {
        var f = StaHost.Run(() =>
        {
            // A table 600 tall behind the state, as a list screen has; the surface either fills the page (a list area) or sizes to what it shows.
            var surface = new LoadSurface
            {
                State = state,
                SkeletonKind = SkeletonKind.List,
                EmptyTitle = "No donors yet",
                ErrorMessage = "Can't reach the server. Check your connection, then retry.",
                Content = new Border { Height = 600 },
                VerticalAlignment = fillsThePage ? VerticalAlignment.Stretch : VerticalAlignment.Top,
            };
            using var host = Hosted.Create(Hosted.Stage(surface), 900, 800);
            host.Settle(200);
            FrameworkElement shown = state == LoadState.Empty ? host.All<EmptyState>().Single(e => e.IsVisible) : host.All<ErrorState>().Single(e => e.IsVisible);
            return (
                Surface: Hosted.BoundsOf(surface, host.Element),
                State: Hosted.BoundsOf(shown, host.Element),
                SkeletonHeight: surface.SkeletonPart?.IsVisible == true ? surface.SkeletonPart.ActualHeight : 0);
        });

        Assert.Equal(f.Surface.Top, f.State.Top, 0); // at the top of the list area, not floating down it
        Assert.Equal(0, f.SkeletonHeight);
        if (!fillsThePage)
        {
            // Neither the hidden table nor the skeleton holds the surface open: it is as tall as the state it shows.
            Assert.Equal(f.State.Height, f.Surface.Height, 0);
        }
    }

    [Fact]
    public void LoadSurface_WhileLoading_KeepsTheContentsPlace()
    {
        var height = StaHost.Run(() =>
        {
            var surface = new LoadSurface { State = LoadState.Loading, SkeletonKind = SkeletonKind.List, Content = new Border { Height = 600 }, VerticalAlignment = VerticalAlignment.Top };
            using var host = Hosted.Create(Hosted.Stage(surface), 900, 800);
            host.Settle(200);
            return (Surface: surface.ActualHeight, Skeleton: surface.SkeletonPart?.IsVisible == true);
        });

        Assert.True(height.Skeleton, "the skeleton is not shown while loading");
        Assert.True(height.Surface >= 600, $"the surface is {height.Surface} tall while loading: the hidden content no longer holds its place");
    }

    [Theory]
    [InlineData(3000)] // the body scrolls
    [InlineData(120)] // it does not
    public void SidePanel_Content_KeepsIts24RightPadding_WithOrWithoutAScrollBar(double contentHeight)
    {
        var f = StaHost.Run(() =>
        {
            var block = new Border { Height = contentHeight };
            var viewer = new ScrollViewer { Content = block, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            viewer.SetResourceReference(FrameworkElement.StyleProperty, "ScrollViewer.Panel");
            var panel = new SidePanel { IsOpen = true, Overline = "Donor D-0175", Content = viewer, HorizontalAlignment = HorizontalAlignment.Right };
            using var host = Hosted.Create(Hosted.Stage(panel), 900, 700);
            host.Settle(400);
            var bar = host.All<ScrollBar>().FirstOrDefault(b => b.Orientation == Orientation.Vertical && b.IsVisible && viewer.IsAncestorOf(b));
            return (
                Panel: Hosted.BoundsOf(panel, host.Element),
                Content: Hosted.BoundsOf(block, host.Element),
                Bar: bar is null ? Rect.Empty : Hosted.BoundsOf(bar, host.Element),
                Inside: SidePanel.GetIsInside(block));
        });

        Assert.True(f.Inside, "the panel does not mark its content (SidePanel.IsInside)");
        Assert.Equal(f.Panel.Right - 24, f.Content.Right, 0);
        Assert.Equal(f.Panel.Left + 1 + 24, f.Content.Left, 0); // the panel's 1 DIP left edge line, then its padding
        Assert.Equal(contentHeight > 700, !f.Bar.IsEmpty);
        if (!f.Bar.IsEmpty)
        {
            // The bar's whole strip lies in the panel's right padding, beside the content and never over it.
            Assert.True(f.Bar.Left >= f.Content.Right - 0.5, $"the scroll bar starts at x {f.Bar.Left}, over the content, which ends at {f.Content.Right}");
            Assert.True(f.Bar.Right <= f.Panel.Right + 0.5, $"the scroll bar ends at x {f.Bar.Right}, outside the panel, which ends at {f.Panel.Right}");
        }
    }

    [Fact]
    public void ScrollViewerPanelStyle_OutsideASidePanel_ChangesNothing()
    {
        var f = StaHost.Run(() =>
        {
            var block = new Border { Height = 100 };
            var viewer = new ScrollViewer { Content = block };
            viewer.SetResourceReference(FrameworkElement.StyleProperty, "ScrollViewer.Panel");
            using var host = Hosted.Create(Hosted.Stage(viewer), 400, 300);
            host.Settle(100);
            return (viewer.Margin, viewer.Padding, Viewer: Hosted.BoundsOf(viewer, host.Element), Content: Hosted.BoundsOf(block, host.Element));
        });

        Assert.Equal(default, f.Margin);
        Assert.Equal(default, f.Padding);
        Assert.Equal(f.Viewer.Right, f.Content.Right, 0);
    }

    [Fact]
    public void Badge_GivenLessRoomThanItsWords_EndsInAnEllipsis_WithTheFullTextAsItsTooltip()
    {
        var f = StaHost.Run(() =>
        {
            var squeezed = new Badge { Text = "Donation receipt", Icon = IconKind.Drop };
            var explained = new Badge { Text = "Protected account", Icon = IconKind.Lock, ToolTip = "The owner account can't be deleted." };
            var roomy = new Badge { Text = "Donation receipt", Icon = IconKind.Drop };
            var narrow = new Border { Width = 70, Child = squeezed, HorizontalAlignment = HorizontalAlignment.Left };
            var narrowToo = new Border { Width = 70, Child = explained, HorizontalAlignment = HorizontalAlignment.Left };
            var stack = new StackPanel { Children = { narrow, narrowToo, roomy } };
            using var host = Hosted.Create(Hosted.Stage(stack), 500, 300);
            host.Settle(150);
            static bool Trimmed(Badge badge) => TruncationTooltip.IsTrimmed((TextBlock)badge.Template.FindName("Label", badge));
            var squeezedWidth = squeezed.ActualWidth;
            var first = (Trimmed: Trimmed(squeezed), squeezed.ToolTip, Explained: explained.ToolTip, RoomyTrimmed: Trimmed(roomy), RoomyTip: roomy.ReadLocalValue(FrameworkElement.ToolTipProperty) != DependencyProperty.UnsetValue);

            narrow.Width = 400; // the column gets its width back
            host.Layout(500, 300);
            host.Settle(150);
            return (first, squeezedWidth, After: (Trimmed: Trimmed(squeezed), HasTip: squeezed.ReadLocalValue(FrameworkElement.ToolTipProperty) != DependencyProperty.UnsetValue));
        });

        Assert.True(f.first.Trimmed, "the squeezed badge's label is not trimmed: it is cut mid-letter or overflows");
        Assert.True(f.squeezedWidth <= 70.5, $"the squeezed badge is {f.squeezedWidth} wide in 70");
        Assert.Equal("Donation receipt", f.first.ToolTip);
        Assert.Equal("The owner account can't be deleted.", f.first.Explained); // a tooltip the screen set is left alone
        Assert.False(f.first.RoomyTrimmed);
        Assert.False(f.first.RoomyTip); // a badge that fits has no tooltip of its own
        Assert.False(f.After.Trimmed);
        Assert.False(f.After.HasTip); // and loses the one it had when its text fits again
    }

    [Fact]
    public void ListTable_TheNameColumnGetsWhatItsNamesNeed_AndTheLowestPriorityColumnFoldsAwayBeforeANameIsCut()
    {
        var f = StaHost.Run(() =>
        {
            var name = new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Person.Name)), Width = DataGridLength.Auto, MinWidth = 120, MaxWidth = 320 };
            var group = new DataGridTextColumn { Header = "Group", Binding = new Binding(nameof(Person.Group)), Width = 80 };
            var detail = new DataGridTextColumn { Header = "Detail", Binding = new Binding(nameof(Person.Detail)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 150 };
            var extra = new DataGridTextColumn { Header = "Extra", Binding = new Binding(nameof(Person.Extra)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 150 };
            ListColumns.SetHideBelow(detail, 100); // folds away last
            ListColumns.SetHideBelow(extra, 200); // the lowest priority: folds away first
            var grid = new LifestreamDataGrid
            {
                Columns = { name, group, detail, extra },
                ItemsSource = new[]
                {
                    new Person("Bartholomew Featherstonehaugh", "O+", "Eligible", "12 Mar 2026"),
                    new Person("Ada Wu", "A−", "Eligible", "03 Aug 2026"),
                },
            };
            ListColumns.SetIsResponsive(grid, true);

            // 520 wide: the long name (about 230), Group (80) and two detail columns of at least 150 need more than the table has.
            using var host = Hosted.Create(Hosted.Stage(grid), 520, 300);
            host.Settle(400);
            bool NameCut() => host.All<TextBlock>().Any(t => t.IsVisible && t.Text.StartsWith("Bartholomew", StringComparison.Ordinal) && TruncationTooltip.IsTrimmed(t));
            var narrow = (Extra: extra.Visibility, Detail: detail.Visibility, NameCut: NameCut(), Name: name.ActualWidth, Total: grid.Columns.Where(c => c.Visibility == Visibility.Visible).Sum(c => c.ActualWidth));

            // 1000 wide: every column is back, the name column is still only as wide as its names, and the two detail columns share the rest equally.
            host.Layout(1000, 300);
            host.Settle(400);
            return (narrow, Wide: (Extra: extra.Visibility, NameCut: NameCut(), Name: name.ActualWidth, Detail: detail.ActualWidth, ExtraWidth: extra.ActualWidth,
                Total: grid.Columns.Where(c => c.Visibility == Visibility.Visible).Sum(c => c.ActualWidth)));
        });

        Assert.Equal(Visibility.Collapsed, f.narrow.Extra); // the lowest-priority column went
        Assert.Equal(Visibility.Visible, f.narrow.Detail);
        Assert.False(f.narrow.NameCut, $"the name is cut at 520 although a column could fold away (name column {f.narrow.Name})");
        Assert.True(f.narrow.Total <= 520.5, $"the columns on show are {f.narrow.Total} wide in a table of 520");

        Assert.Equal(Visibility.Visible, f.Wide.Extra);
        Assert.False(f.Wide.NameCut);
        Assert.True(f.Wide.Name < 320, $"the name column is {f.Wide.Name} wide: it took spare width that belongs to the detail columns");
        Assert.Equal(f.Wide.Detail, f.Wide.ExtraWidth, 0); // the spare width is shared between the detail columns
        Assert.Equal(1000, f.Wide.Total, 0); // and the table is full: no blank strip at its right edge
    }

    [Fact]
    public void ListTable_WithNoDetailColumnOnShow_GivesTheSpareWidthToTheNameColumn()
    {
        var f = StaHost.Run(() =>
        {
            var name = new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Person.Name)), Width = DataGridLength.Auto, MinWidth = 120, MaxWidth = 320 };
            var group = new DataGridTextColumn { Header = "Group", Binding = new Binding(nameof(Person.Group)), Width = 80 };
            var detail = new DataGridTextColumn { Header = "Detail", Binding = new Binding(nameof(Person.Detail)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 150 };
            ListColumns.SetHideBelow(detail, 600);
            var grid = new LifestreamDataGrid { Columns = { name, group, detail }, ItemsSource = new[] { new Person("Ada Wu", "A−", "Eligible", string.Empty) } };
            ListColumns.SetIsResponsive(grid, true);
            using var host = Hosted.Create(Hosted.Stage(grid), 360, 300);
            host.Settle(400);
            var narrow = (Detail: detail.Visibility, Name: name.ActualWidth, Group: group.ActualWidth);
            host.Layout(900, 300);
            host.Settle(400);
            return (narrow, Wide: (Detail: detail.Visibility, Name: name.ActualWidth, DetailWidth: detail.ActualWidth));
        });

        // 360 wide: the detail column is folded away (below its 600), and the name column fills what Group leaves, up to its cap.
        Assert.Equal(Visibility.Collapsed, f.narrow.Detail);
        Assert.Equal(360 - 80, f.narrow.Name, 0);

        // Wide again: the detail column is back and takes the spare width; the name column is as wide as its names (its minimum here).
        Assert.Equal(Visibility.Visible, f.Wide.Detail);
        Assert.Equal(120, f.Wide.Name, 0);
        Assert.Equal(900 - 80 - 120, f.Wide.DetailWidth, 0);
    }
}
