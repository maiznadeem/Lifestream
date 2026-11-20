using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Lifestream.Client.Common;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Services;
using Lifestream.Desktop.Theme;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// The shared-control findings of the design review of direction A (run/agents/design-qa.md, items 12, 16, 19, 21, 27, 28 and 40), each pinned on the
/// real styles: one error look and no WPF adorner, initials whenever there is no real photo, an avatar that stays visible on a selected row, Blood text
/// on the large tag, the selected tab's bar, hover fills that show on the canvas, read-only text in ink, the page header's count and the side panel's
/// bottom inset.
/// </summary>
[Collection("Wpf")]
public sealed class DesignReviewTests
{
    private static Color Token(FrameworkElement scope, string key) => ((SolidColorBrush)scope.FindResource(key)).Color;

    private static Grid Stage(double width, double height, params UIElement[] children)
    {
        var grid = new Grid { Width = width, Height = height };
        grid.SetResourceReference(Panel.BackgroundProperty, "Brush.Canvas");
        grid.SetResourceReference(TextElement.ForegroundProperty, "Brush.Ink");
        foreach (var child in children)
        {
            grid.Children.Add(child);
        }

        return grid;
    }

    private sealed class Holder
    {
        public object? Value { get; set; }
    }

    private sealed class AlwaysWrong : ValidationRule
    {
        public override ValidationResult Validate(object value, System.Globalization.CultureInfo cultureInfo) => new(false, "Wrong.");
    }

    /// <summary>Puts a control in WPF's validation error state, as a failed binding does.</summary>
    private static void MarkInvalid(FrameworkElement element, DependencyProperty property)
    {
        var binding = new Binding(nameof(Holder.Value)) { Source = new Holder(), Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit };
        var expression = BindingOperations.SetBinding(element, property, binding);
        Validation.MarkInvalid(expression, new ValidationError(new AlwaysWrong(), expression, "Wrong.", null));
    }

    /// <summary>The visible box of an input (the "Box" edge of the field templates) and whether its 2 px error bar shows.</summary>
    private static (Color Edge, bool Bar) Look(FrameworkElement input)
    {
        var box = Hosted.Descendants(input).OfType<Border>().First(b => b.Name == "Box" && b.IsVisible);
        // A composite input (the password field) holds two boxes, one of them collapsed: the bar of the box that is on screen.
        var bar = Hosted.Descendants(input).OfType<FrameworkElement>().First(e => e.Name == "ErrorBar" && IsInVisibleBranch(e, input));
        return (((SolidColorBrush)box.BorderBrush).Color, bar.IsVisible);
    }

    private static bool IsInVisibleBranch(FrameworkElement element, FrameworkElement root)
    {
        for (DependencyObject? node = VisualTreeHelper.GetParent(element); node is not null && !ReferenceEquals(node, root); node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    // 12

    [Fact]
    public void EveryInput_InError_ShowsTheCriticalEdgeAndBar_OfItsOwnStyle_AndNeverWpfsRedAdorner()
    {
        var f = StaHost.Run(() =>
        {
            ThemeService.Apply(ThemeMode.Light);

            // In WPF's own error state (a binding that failed validation)...
            var text = new TextBox { Text = "x", Width = 200 };
            var password = new PasswordField { Width = 200 };
            var combo = new ComboBox { Width = 200, ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 };
            var date = new DateField { Width = 200 };
            var check = new CheckBox { Content = "Agree" };
            // ...and inside a FormField that shows an error (no validation on the input itself).
            var inField = new TextBox { Text = "+1-555-01" };
            var passwordInField = new PasswordField();
            var comboInField = new ComboBox { ItemsSource = new[] { "One" }, SelectedIndex = 0 };
            var dateInField = new DateField();
            var atRest = new TextBox { Text = "fine", Width = 200 };

            var column = new StackPanel { Width = 260, Margin = new Thickness(20) };
            foreach (var input in new FrameworkElement[] { text, password, combo, date, check, atRest })
            {
                input.Margin = new Thickness(0, 0, 0, 12);
                column.Children.Add(input);
            }

            foreach (var input in new FrameworkElement[] { inField, passwordInField, comboInField, dateInField })
            {
                column.Children.Add(new FormField { Label = "Field", Error = "Say what is wrong.", Content = input });
            }

            // An adorner layer, as every window has: WPF would draw its red rectangle here.
            var decorator = new AdornerDecorator { Child = column };
            using var host = Hosted.Create(Stage(320, 900, decorator), 320, 900);
            host.Settle(100);
            MarkInvalid(text, TextBox.TextProperty);
            MarkInvalid(password, PasswordField.PasswordProperty);
            MarkInvalid(combo, Selector.SelectedItemProperty);
            MarkInvalid(date, DateField.SelectedDateProperty);
            MarkInvalid(check, ToggleButton.IsCheckedProperty);
            host.Settle(150);

            var layer = AdornerLayer.GetAdornerLayer(column)!;
            var invalid = new FrameworkElement[] { text, password, combo, date, check };
            return (
                HasError: invalid.Select(Validation.GetHasError).ToList(),
                Templates: invalid.Select(i => Validation.GetErrorTemplate(i) is null).ToList(),
                Adorners: invalid.Sum(i => layer.GetAdorners(i)?.Length ?? 0) + VisualTreeHelper.GetChildrenCount(layer),
                Looks: new FrameworkElement[] { text, password, combo, date, inField, passwordInField, comboInField, dateInField }.Select(Look).ToList(),
                AtRest: Look(atRest),
                Critical: Token(column, "Brush.Critical"),
                Stroke2: Token(column, "Brush.Stroke2"));
        });

        Assert.All(f.HasError, Assert.True); // the controls really are in WPF's error state
        Assert.All(f.Templates, noTemplate => Assert.True(noTemplate, "an input still has WPF's error template (the red adorner rectangle)"));
        Assert.Equal(0, f.Adorners);
        Assert.All(f.Looks, look => Assert.Equal(f.Critical, look.Edge)); // the field's own edge, in the Critical token
        Assert.All(f.Looks, look => Assert.True(look.Bar, "the 2 px error bar is not shown"));
        Assert.Equal(f.Stroke2, f.AtRest.Edge); // a field with no error is untouched
        Assert.False(f.AtRest.Bar);
    }

    // 40 (read-only)

    [Fact]
    public void ReadOnlyField_KeepsInkText_OnAQuietFill_AndOnlyADisabledFieldIsMuted()
    {
        var f = StaHost.Run(() =>
        {
            ThemeService.Apply(ThemeMode.Light);
            var readOnly = new TextBox { Text = "D-0175", IsReadOnly = true, Width = 160, VerticalAlignment = VerticalAlignment.Top };
            var disabled = new TextBox { Text = "S-0002", IsEnabled = false, Width = 160, VerticalAlignment = VerticalAlignment.Bottom };
            using var host = Hosted.Create(Stage(240, 140, readOnly, disabled), 240, 140);
            host.Settle(100);
            Color Fill(TextBox box) => ((SolidColorBrush)Hosted.Descendants(box).OfType<Border>().First(b => b.Name == "Box").Background).Color;
            return (ReadOnly: ((SolidColorBrush)readOnly.Foreground).Color, Disabled: ((SolidColorBrush)disabled.Foreground).Color, ReadOnlyFill: Fill(readOnly),
                Ink: Token(readOnly, "Brush.Ink"), Ink3: Token(readOnly, "Brush.Ink3"), Layer2: Token(readOnly, "Brush.Layer2"));
        });

        Assert.Equal(f.Ink, f.ReadOnly);
        Assert.Equal(f.Layer2, f.ReadOnlyFill);
        Assert.Equal(f.Ink3, f.Disabled);
    }

    // 16, 28

    [Fact]
    public void Avatar_ShowsInitialsForThePlaceholderDrawing_AndStaysVisibleOnASelectedRow()
    {
        var f = StaHost.Run(() =>
        {
            ThemeService.Apply(ThemeMode.Light);

            // The 2.0 converter returns a shared placeholder drawing for "no photo": that is not a photo.
            var placeholder = new Avatar { Initials = "SS", Source = DefaultAvatar.Image, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var viaConverter = new Avatar { Initials = "TU", Source = (ImageSource)new AvatarConverter().Convert(null!, typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };

            // A table whose first column is an avatar, as Donors, Patients, Appointments and Staff have.
            var grid = new LifestreamDataGrid { Width = 400, Height = 200, VerticalAlignment = VerticalAlignment.Bottom, ItemsSource = new[] { "AM", "CB", "NF" } };
            var cell = new FrameworkElementFactory(typeof(Avatar));
            cell.SetBinding(Avatar.InitialsProperty, new Binding());
            grid.Columns.Add(new DataGridTemplateColumn { Header = "Name", CellTemplate = new DataTemplate { VisualTree = cell }, Width = 200 });
            using var host = Hosted.Create(Stage(400, 300, placeholder, viaConverter, grid), 400, 300);
            host.Settle(200);
            grid.SelectedIndex = 1;
            host.Settle(150);

            Color Fill(DataGridRow row) => ((SolidColorBrush)Hosted.Descendants(row).OfType<Avatar>().Single().Background).Color;
            var rows = host.All<DataGridRow>().ToList();
            bool InitialsShown(Avatar avatar) => Hosted.Descendants(avatar).OfType<TextBlock>().Single().IsVisible;
            return (Placeholder: (placeholder.HasPhoto, InitialsShown(placeholder)), Converter: (viaConverter.HasPhoto, InitialsShown(viaConverter)),
                Selected: Fill(rows[1]), Other: Fill(rows[0]), RowFill: Token(grid, "Brush.Selected"), Layer: Token(grid, "Brush.Layer"));
        });

        Assert.Equal((false, true), f.Placeholder); // initials, never a blank disc
        Assert.Equal((false, true), f.Converter);
        Assert.Equal(f.RowFill, f.Other); // at rest: initials on Selected
        Assert.Equal(f.Layer, f.Selected); // on the selected row (itself filled with Selected) the disc is Layer
        Assert.NotEqual(f.RowFill, f.Selected);
    }

    // 27

    [Fact]
    public void BloodGroupTag_SmallAndLarge_AreBloodText_WhateverTheTextColourAroundThem()
    {
        var f = StaHost.Run(() =>
        {
            ThemeService.Apply(ThemeMode.Light);
            var small = new BloodGroupTag { Text = "O−", HorizontalAlignment = HorizontalAlignment.Left };
            var large = new BloodGroupTag { Text = "O−", IsLarge = true, HorizontalAlignment = HorizontalAlignment.Right };
            using var host = Hosted.Create(Stage(200, 80, small, large), 200, 80); // the stage sets the ink text colour, as a panel header does
            host.Settle(100);
            Color Text(BloodGroupTag tag) => ((SolidColorBrush)Hosted.Descendants(tag).OfType<TextBlock>().Single().Foreground).Color;
            return (Small: Text(small), Large: Text(large), LargeHeight: large.ActualHeight, Blood: Token(small, "Brush.Blood"), Ink: Token(small, "Brush.Ink"));
        });

        Assert.Equal(f.Blood, f.Small);
        Assert.Equal(f.Blood, f.Large);
        Assert.NotEqual(f.Ink, f.Large);
        Assert.Equal(32, f.LargeHeight);
    }

    // 40 (tab bar, hover)

    [Fact]
    public void SelectedTab_HasTheSelectedBar_NotBlood()
    {
        var f = StaHost.Run(() =>
        {
            ThemeService.Apply(ThemeMode.Light);
            var on = new RadioButton { Content = "All 8", IsChecked = true, GroupName = "t", HorizontalAlignment = HorizontalAlignment.Left };
            var off = new RadioButton { Content = "Low 2", GroupName = "t", HorizontalAlignment = HorizontalAlignment.Right };
            on.SetResourceReference(FrameworkElement.StyleProperty, "Tab.Option");
            off.SetResourceReference(FrameworkElement.StyleProperty, "Tab.Option");
            var tabs = new TabControl { VerticalAlignment = VerticalAlignment.Bottom };
            tabs.SetResourceReference(FrameworkElement.StyleProperty, "Tabs");
            foreach (var header in new[] { "One", "Two" })
            {
                var item = new TabItem { Header = header, Content = new TextBlock { Text = header } };
                item.SetResourceReference(FrameworkElement.StyleProperty, "Tabs.Item");
                tabs.Items.Add(item);
            }

            using var host = Hosted.Create(Stage(300, 160, on, off, tabs), 300, 160);
            host.Settle(150);
            Border Bar(Control control) => (Border)control.Template.FindName("Bar", control);
            var item0 = (TabItem)tabs.Items[0]!;
            return (On: (Bar(on).IsVisible, ((SolidColorBrush)Bar(on).Background).Color), Off: Bar(off).IsVisible, Item: (Bar(item0).IsVisible, ((SolidColorBrush)Bar(item0).Background).Color),
                SelectedBar: Token(on, "Brush.SelectedBar"), Blood: Token(on, "Brush.Blood"));
        });

        Assert.Equal((true, f.SelectedBar), f.On);
        Assert.False(f.Off);
        Assert.Equal((true, f.SelectedBar), f.Item);
        Assert.NotEqual(f.Blood, f.SelectedBar); // red means blood; a tab is navigation
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void SubtleAndSecondaryButtons_OnThePageCanvas_ShowTheirHover_AndSecondaryKeepsItsEdge(string theme)
    {
        var f = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            var subtle = new Button { Content = "Cancel", Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
            subtle.SetResourceReference(FrameworkElement.StyleProperty, "Button.Subtle");
            var secondary = new Button { Content = "Edit donor", Width = 120, HorizontalAlignment = HorizontalAlignment.Right };
            secondary.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
            var stage = Stage(320, 80, subtle, secondary);
            using var host = Hosted.Create(stage, 320, 80);
            host.Settle(100);

            (int R, int G, int B) At(byte[] pixels, double x, double y)
            {
                var i = (((int)y * 320) + (int)x) * 4;
                return (pixels[i + 2], pixels[i + 1], pixels[i]);
            }

            var subtleBounds = Hosted.BoundsOf(subtle, stage);
            var secondaryBounds = Hosted.BoundsOf(secondary, stage);
            // A point of the fill well away from the label (near the left end), and the middle of the left edge.
            static (double X, double Y) FillOf(Rect b) => (b.Left + 6, b.Top + (b.Height / 2));
            var rest = host.Pixels();
            VisualStateManager.GoToState(subtle, "MouseOver", false);
            VisualStateManager.GoToState(secondary, "MouseOver", false);
            host.Settle(150);
            var hover = host.Pixels();
            var (sx, sy) = FillOf(subtleBounds);
            var (ex, ey) = FillOf(secondaryBounds);
            return (SubtleRest: At(rest, sx, sy), SubtleHover: At(hover, sx, sy), SecondaryRest: At(rest, ex, ey), SecondaryHover: At(hover, ex, ey),
                EdgeRest: At(rest, secondaryBounds.Left, ey), EdgeHover: At(hover, secondaryBounds.Left, ey), Stroke2: Token(stage, "Brush.Stroke2"));
        });

        static int Distance((int R, int G, int B) a, (int R, int G, int B) b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        // The hover fill differs from what is behind it by enough to see (the opaque Hover token was 3 levels from the light canvas: invisible).
        Assert.True(Distance(f.SubtleRest, f.SubtleHover) >= 24, $"{theme}: subtle hover {f.SubtleHover} on {f.SubtleRest}");
        Assert.True(Distance(f.SecondaryRest, f.SecondaryHover) >= 12, $"{theme}: secondary hover {f.SecondaryHover} on {f.SecondaryRest}");
        // The secondary button's 1 px edge is still there under the hover fill.
        Assert.Equal((f.Stroke2.R, f.Stroke2.G, f.Stroke2.B), ((byte)f.EdgeRest.R, (byte)f.EdgeRest.G, (byte)f.EdgeRest.B));
        Assert.Equal(f.EdgeRest, f.EdgeHover);
    }

    // 21

    private sealed class Page(LoadState state)
    {
        public LoadState State { get; } = state;
    }

    [Theory]
    [InlineData(LoadState.Ready, true)]
    [InlineData(LoadState.Empty, true)]
    [InlineData(LoadState.Loading, false)]
    [InlineData(LoadState.Waking, false)]
    [InlineData(LoadState.Error, false)]
    public void PageHeader_ShowsItsCount_OnlyWhenThePageHasLoaded(LoadState state, bool shown)
    {
        var f = StaHost.Run(() =>
        {
            // As in a list screen: the header binds its count, and reads the page's state from the screen's view model without being told to.
            var bound = new PageHeader { Title = "Receipts", Count = "0", DataContext = new Page(state), VerticalAlignment = VerticalAlignment.Top };
            var set = new PageHeader { Title = "Receipts", Count = "0", State = state, VerticalAlignment = VerticalAlignment.Center };
            var noState = new PageHeader { Title = "Receipts", Count = "40", DataContext = "a view model with no State", VerticalAlignment = VerticalAlignment.Bottom };
            using var host = Hosted.Create(Stage(500, 300, bound, set, noState), 500, 300);
            host.Settle(100);
            bool Count(PageHeader header) => ((FrameworkElement)header.Template.FindName("CountBadge", header)).IsVisible;
            return (Bound: Count(bound), Set: Count(set), NoState: Count(noState));
        });

        Assert.Equal(shown, f.Bound);
        Assert.Equal(shown, f.Set);
        Assert.True(f.NoState); // a page with no load state shows its count
    }

    // 19

    [Fact]
    public void SidePanel_KeepsRoomUnderItsContent_SoAStickyFooterDoesNotTouchTheWindowEdge()
    {
        var f = StaHost.Run(() =>
        {
            // A form in the panel: fields that take the room, then the divider and the buttons at the bottom.
            var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 16, 0, 0), Child = new Button { Content = "Save changes", HorizontalAlignment = HorizontalAlignment.Right } };
            footer.SetResourceReference(Border.BorderBrushProperty, "Brush.Stroke");
            var form = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(footer, Dock.Bottom);
            form.Children.Add(footer);
            form.Children.Add(new TextBlock { Text = "Fields" });
            var panel = new SidePanel { IsOpen = true, Content = form, HorizontalAlignment = HorizontalAlignment.Right };
            using var host = Hosted.Create(Stage(600, 500, panel), 600, 500);
            host.Settle(300);
            var button = (Button)footer.Child;
            var buttonBounds = Hosted.BoundsOf(button, panel);
            var footerBounds = Hosted.BoundsOf(footer, panel);
            return (Below: panel.ActualHeight - buttonBounds.Bottom, DividerAbove: buttonBounds.Top - footerBounds.Top, panel.ActualHeight);
        });

        Assert.InRange(f.Below, 16, 24); // air under the buttons
        Assert.True(f.DividerAbove >= 16, "the divider stays above the buttons");
        Assert.Equal(500, f.ActualHeight);
    }

    // The wrap of a sentence that names a blood group (dashboard, item 10)

    [Fact]
    public void KeepGroupsTogether_ASentenceNeverBreaksBeforeAGroupsMinusSign()
    {
        var f = StaHost.Run(() =>
        {
            const string Sentence = "Nadia Holloway needs 2 units; Gideon Achterberg (O−) comes in at 11:30 and Hana (AB−) at 12:00.";
            List<string> LineStarts(string text)
            {
                var starts = new List<string>();
                for (var width = 400; width >= 120; width -= 3)
                {
                    var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Width = width, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                    using var host = Hosted.Create(Stage(420, 300, block), 420, 300);
                    host.Settle(1);
                    for (var line = 1; block.ContentStart.GetLineStartPosition(line) is { } start && line < 20; line++)
                    {
                        starts.Add(new TextRange(start, block.ContentEnd).Text);
                    }
                }

                return starts;
            }

            return (Plain: LineStarts(Sentence), Kept: LineStarts(KeepGroupsTogetherConverter.Join(Sentence)), Joined: KeepGroupsTogetherConverter.Join(Sentence), Twice: KeepGroupsTogetherConverter.Join(KeepGroupsTogetherConverter.Join(Sentence)));
        });

        // The control: without the converter WPF does start a line with the minus sign at some width (the defect seen on the dashboard).
        Assert.Contains(f.Plain, line => line.StartsWith('−'));
        Assert.DoesNotContain(f.Kept, line => line.StartsWith('−') || line.StartsWith('⁠'));
        Assert.Equal(f.Joined, f.Twice); // applying it twice adds nothing
        Assert.Equal(2, f.Joined.Count(c => c == '⁠'));
    }
}
