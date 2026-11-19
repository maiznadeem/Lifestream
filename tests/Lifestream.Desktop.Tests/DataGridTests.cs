using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Tests;

[Collection("Wpf")]
public sealed class DataGridTests
{
    public sealed record Row(int Id, string Name, string Phone, string Status);

    private static LifestreamDataGrid Grid(IEnumerable<Row> rows, double width = 900)
    {
        var grid = new LifestreamDataGrid { Width = width, Height = 560, ItemsSource = rows.ToList() };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)), SortMemberPath = "name" });
        grid.Columns.Add(new DataGridTextColumn { Header = "Phone", Binding = new Binding(nameof(Row.Phone)), SortMemberPath = "phone", Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(Row.Status)), SortMemberPath = "status" });
        return grid;
    }

    private static List<Row> Rows(int count) => [.. Enumerable.Range(1, count).Select(i => new Row(i, $"Donor {i} Ayşe Öztürk", $"+1-555-01{i % 100:00}", i % 2 == 0 ? "Completed" : "Pending"))];

    private static Grid Stage(UIElement child, double width, double height)
    {
        var grid = new Grid { Width = width, Height = height };
        grid.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
        grid.Children.Add(child);
        return grid;
    }

    [Fact]
    public void DataGrid_10000Items_CreatesUnder300Rows()
    {
        var (created, realized, total) = StaHost.Run(() =>
        {
            var grid = Grid(Rows(10_000));
            var seen = new HashSet<DataGridRow>();
            grid.LoadingRow += (_, e) => seen.Add(e.Row);
            using var host = Hosted.Create(Stage(grid, 900, 560), 900, 560);
            host.Settle(400);

            // Scroll to the middle and the end: recycling must keep the count of row objects small.
            var scroll = host.All<ScrollViewer>().First(s => s.ScrollableHeight > 0);
            foreach (var fraction in new[] { 0.25, 0.5, 0.99 })
            {
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * fraction);
                host.Settle(150);
            }

            host.Capture("DataGrid_10000_Scrolled.png");
            return (seen.Count, host.All<DataGridRow>().Count(), grid.Items.Count);
        });

        Assert.Equal(10_000, total);
        Assert.InRange(created, 1, 299);
        Assert.InRange(realized, 1, 299);
    }

    [Fact]
    public void DataGrid_RowsAre44Dip_HeaderIs38_TextIs14Dip_SeparatorsOnly_SelectionIsTheSelectedTokenWithA3DipBar()
    {
        var (rowHeight, fontSize, verticalLine, rowLine, lines, headerHeight, minRow, selected) = StaHost.Run(() =>
        {
            var grid = Grid(Rows(6));
            using var host = Hosted.Create(Stage(grid, 900, 400), 900, 400);
            host.Settle(300);
            var row = host.All<DataGridRow>().First();
            var cellText = host.All<TextBlock>().First(t => t.Text.StartsWith("Donor 1 ", StringComparison.Ordinal));
            var pixels = host.Pixels();
            var background = Pixel(pixels, 900, 450, 150);

            // A vertical gridline would sit between two columns, inside a row. Sample the boundary of the Phone column in row 2.
            var phoneHeader = host.All<System.Windows.Controls.Primitives.DataGridColumnHeader>().First(h => (string)h.Content == "Phone");
            var boundary = Hosted.BoundsOf(phoneHeader, host.Element).Left;
            var rowBounds = Hosted.BoundsOf(host.All<DataGridRow>().ElementAt(1), host.Element);
            var verticalSample = Pixel(pixels, 900, (int)boundary, (int)(rowBounds.Top + 20));
            var separatorSample = Pixel(pixels, 900, 450, (int)rowBounds.Bottom - 1);

            grid.SelectedIndex = 2;
            host.Settle(150);
            host.Capture("DataGrid_Selected.png");
            var selectedBounds = Hosted.BoundsOf(host.All<DataGridRow>().ElementAt(2), host.Element);
            var selectedRow = host.All<DataGridRow>().ElementAt(2);
            var selectedPixels = host.Pixels();
            var bar = (FrameworkElement)selectedRow.Template.FindName("SelectedBar", selectedRow);
            var barBounds = Hosted.BoundsOf(bar, host.Element);
            Color Token(string key) => ((SolidColorBrush)host.Element.TryFindResource(key)).Color;
            var otherBar = (FrameworkElement)host.All<DataGridRow>().ElementAt(1).Template.FindName("SelectedBar", host.All<DataGridRow>().ElementAt(1));
            var selected = (
                Fill: Pixel(selectedPixels, 900, 700, (int)(selectedBounds.Top + 20)), // away from the bar and the text
                Expected: Token("Brush.Selected"),
                BarPixel: Pixel(selectedPixels, 900, (int)barBounds.Left + 1, (int)(selectedBounds.Top + 20)),
                BarToken: Token("Brush.SelectedBar"),
                BarWidth: bar.ActualWidth,
                BarLeft: barBounds.Left - selectedBounds.Left,
                BarVisible: bar.IsVisible,
                OtherRowBarVisible: otherBar.IsVisible);
            return (row.ActualHeight, cellText.FontSize, verticalSample == background, separatorSample != background, grid.GridLinesVisibility, phoneHeader.ActualHeight, grid.MinRowHeight, selected);
        });

        // Direction A: rows are 44 DIP (compact 34 is for dense panels), the header 38.
        Assert.Equal(44, rowHeight, 0.5);
        Assert.Equal(44, minRow);
        Assert.Equal(14, fontSize);
        Assert.True(verticalLine, "no vertical separator between columns");
        Assert.True(rowLine, "a separator line under each row");
        Assert.Equal(DataGridGridLinesVisibility.None, lines);
        Assert.Equal(38, headerHeight, 0.5);

        // The selected row is the Selected token (which keeps 4.5:1 with the text, ThemeTests) with a 3 DIP SelectedBar inside its left edge.
        Assert.Equal(selected.Expected, selected.Fill);
        Assert.True(selected.BarVisible);
        Assert.False(selected.OtherRowBarVisible, "only the selected row shows the bar");
        Assert.Equal(3, selected.BarWidth, 0.5);
        Assert.Equal(0, selected.BarLeft, 0.5);
        Assert.Equal(selected.BarToken, selected.BarPixel);
    }

    private static Color Pixel(byte[] bgra, int width, int x, int y)
    {
        var i = ((y * width) + x) * 4;
        return Color.FromRgb(bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    [Fact]
    public void DataGrid_SortIndicators_FollowViewModelState_AndSurviveANewPageOfItems()
    {
        var (ascending, descending, afterFilter, commandCalls, other) = StaHost.Run(() =>
        {
            var calls = new List<object?>();
            var grid = Grid(Rows(5));
            grid.SortCommand = new RelayStub(calls.Add);
            grid.SortColumn = "name";
            using var host = Hosted.Create(Stage(grid, 900, 400), 900, 400);
            host.Settle(300);

            // Direction A draws the indicator as an Icon (a 13 px chevron, Chevd): pointing down for descending, turned 180 degrees for ascending.
            (bool Visible, string Kind, double Angle) Indicator(string header)
            {
                var headerElement = host.All<System.Windows.Controls.Primitives.DataGridColumnHeader>().First(h => (string)h.Content == header);
                var icon = FindByName<Icon>(headerElement, "SortGlyph")!;
                var angle = icon.RenderTransform is RotateTransform rotate ? rotate.Angle : 0;
                return (icon.IsVisible, icon.Kind.ToString(), angle);
            }

            var asc = Indicator("Name");
            grid.SortDescending = true;
            host.Settle(100);
            var desc = Indicator("Name");
            host.Capture("DataGrid_SortIndicator.png");

            // "Filtering and paging" replace the items; the sort belongs to the view model, so the indicator stays.
            grid.ItemsSource = Rows(3);
            host.Settle(150);
            var afterItems = Indicator("Name");

            // Clicking another header asks for that sort through the command and moves the indicator.
            var phone = host.All<System.Windows.Controls.Primitives.DataGridColumnHeader>().First(h => (string)h.Content == "Phone");
            // The header is a button: what a click does is its protected OnClick, which raises the grid's Sorting event.
            typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(phone, null);
            host.Settle(100);
            return (asc, desc, afterItems, calls.ToList(), Indicator("Phone"));
        });

        Assert.True(ascending.Visible);
        Assert.Equal("Chevd", ascending.Kind);
        Assert.Equal(180, ascending.Angle);
        Assert.True(descending.Visible);
        Assert.Equal("Chevd", descending.Kind);
        Assert.Equal(0, descending.Angle);
        Assert.True(afterFilter.Visible);
        Assert.Equal(0, afterFilter.Angle); // still the descending sort set above
        Assert.Equal(["phone"], commandCalls.Select(c => c?.ToString()));
        Assert.False(other.Visible); // the view model has not changed the sort, so no other header shows an indicator
    }

    private sealed class RelayStub(Action<object?> execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }

    private static T? FindByName<T>(DependencyObject root, string name)
        where T : FrameworkElement => Hosted.Descendants(root).OfType<T>().FirstOrDefault(e => e.Name == name);

    [Fact]
    public void DataGrid_ColumnsSizeToContent_WithMinimumWidths_AndTruncatedCellsShowATooltip()
    {
        var (nameWidth, phoneWidth, minimums, truncated, fits, tooltipForTruncated) = StaHost.Run(() =>
        {
            var rows = new List<Row> { new(1, new string('W', 120), "+1-555-0100", "Pending"), new(2, "Al", "+1-555-0101", "Pending") };
            var grid = new LifestreamDataGrid { Width = 500, Height = 300, ItemsSource = rows };
            grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)), Width = DataGridLength.SizeToCells, MaxWidth = 260 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Phone", Binding = new Binding(nameof(Row.Phone)), Width = DataGridLength.SizeToCells });
            grid.Columns.Add(new DataGridTextColumn { Header = "St", Binding = new Binding(nameof(Row.Status)), Width = DataGridLength.SizeToHeader });
            using var host = Hosted.Create(Stage(grid, 500, 300), 500, 300);
            host.Settle(400);
            host.Capture("DataGrid_ColumnsAndTruncation.png");

            var blocks = host.All<TextBlock>().Where(t => t.Text.Length > 0 && TruncationTooltip.GetIsEnabled(t)).ToList();
            Assert.True(blocks.Count > 0, "no styled cell text; texts: " + string.Join("|", host.All<TextBlock>().Select(t => t.Text + ":" + TruncationTooltip.GetIsEnabled(t))));
            var longBlock = blocks.First(b => b.Text.StartsWith("WWWW", StringComparison.Ordinal));
            var shortBlock = blocks.First(b => b.Text == "Al");
            return (grid.Columns[0].ActualWidth, grid.Columns[1].ActualWidth, grid.Columns.Select(c => c.MinWidth).ToList(), TruncationTooltip.IsTrimmed(longBlock), TruncationTooltip.IsTrimmed(shortBlock), longBlock.ToolTip is not null);
        });

        Assert.InRange(nameWidth, 200, 262); // sized to the long content, capped by MaxWidth
        Assert.InRange(phoneWidth, 64, 200);  // sized to its content, not stretched
        Assert.All(minimums, m => Assert.True(m >= 64));
        Assert.True(truncated);
        Assert.False(fits); // a cell whose text fits gets no tooltip
        Assert.True(tooltipForTruncated);
    }

    [Fact]
    public void DataGrid_KeyboardRowNavigation_AndTheGridIsFocusable()
    {
        var (focusable, tabNavigation, selectionMode, headersVisible) = StaHost.Run(() =>
        {
            var grid = Grid(Rows(5));
            using var host = Hosted.Create(Stage(grid, 900, 400), 900, 400);
            host.Settle(200);
            return (grid.Focusable && host.All<DataGridCell>().All(c => c.Focusable), KeyboardNavigation.GetTabNavigation(grid), grid.SelectionMode, grid.HeadersVisibility);
        });

        Assert.True(focusable); // the grid and every cell can take keyboard focus
        Assert.NotEqual(KeyboardNavigationMode.None, tabNavigation); // Tab enters the grid and arrow keys move between cells and rows
        Assert.Equal(DataGridSelectionMode.Single, selectionMode);
        Assert.Equal(DataGridHeadersVisibility.Column, headersVisible);
    }

    [Fact]
    public void DataGrid_ObservableItems_StayVirtualised_WhenRowsAreAdded()
    {
        var created = StaHost.Run(() =>
        {
            var items = new ObservableCollection<Row>(Rows(5_000));
            var grid = Grid(items);
            grid.ItemsSource = items;
            var seen = new HashSet<DataGridRow>();
            grid.LoadingRow += (_, e) => seen.Add(e.Row);
            using var host = Hosted.Create(Stage(grid, 900, 560), 900, 560);
            host.Settle(300);
            for (var i = 0; i < 2_000; i++)
            {
                items.Add(new Row(10_000 + i, "Added", "+1-555-0100", "Pending"));
            }

            host.Settle(300);
            return seen.Count;
        });

        Assert.InRange(created, 1, 299);
    }
}
