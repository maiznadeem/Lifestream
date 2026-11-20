using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Theme;
using ThemeMode = Lifestream.Client.Common.ThemeMode;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// The component board of direction A (directions.html, "Components"), rendered from the real styles in both themes: every button variant in its rest,
/// hover, pressed, focus, disabled and busy states, fields at rest, focused, in error and disabled, selection controls, badges, a toast, an empty state
/// and a skeleton. Saved for review next to the mock-up; the test checks that every piece built its template.
/// </summary>
// In the "Wpf" collection: these tests switch the app's theme and build the shell, both process-wide (the theme, and the dependency property descriptors
// the shell hooks its breakpoint on); beside another class doing the same on its own thread they corrupt that shared state (seen under CI's order seeds).
[Trait("Category", "RenderA")]
[Collection("Wpf")]
public sealed class GalleryTests
{
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void ComponentBoard_RendersEveryStateFromTheStyles(string theme)
    {
        var count = StaHost.Run(() =>
        {
            ThemeService.Apply(theme == "dark" ? ThemeMode.Dark : ThemeMode.Light);
            var board = new StackPanel { Margin = new Thickness(28) };
            board.SetResourceReference(TextElement.FontFamilyProperty, "Font.Family");
            board.SetResourceReference(TextElement.FontSizeProperty, "FontSize.Body");
            board.SetResourceReference(TextElement.ForegroundProperty, "Brush.Ink");
            var root = new Border { Child = board };
            root.SetResourceReference(Border.BackgroundProperty, "Brush.Canvas");
            TextDefaults.ApplyTo(root); // as every window of the app

            var states = new[] { "Rest", "Hover", "Pressed", "Focus", "Disabled", "Busy" };
            foreach (var (style, label, icon) in new[] { ("Button.Primary", "Record donation", IconKind.Drop), ("Button.Secondary", "Edit donor", IconKind.Edit), ("Button.Subtle", "Cancel", IconKind.None), ("Button.Danger", "Delete donor", IconKind.Trash) })
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
                foreach (var state in states)
                {
                    var content = new StackPanel { Orientation = Orientation.Horizontal };
                    if (icon != IconKind.None)
                    {
                        content.Children.Add(new Icon { Kind = icon, Size = 16, Margin = new Thickness(0, 0, 8, 0) });
                    }

                    content.Children.Add(new TextBlock { Text = label });
                    var button = new Button { Content = content, Tag = state, Margin = new Thickness(0, 0, 14, 0), IsEnabled = state != "Disabled" };
                    button.SetResourceReference(FrameworkElement.StyleProperty, style);
                    if (state == "Busy")
                    {
                        ButtonAssist.SetIsBusy(button, true);
                        ButtonAssist.SetBusyText(button, style == "Button.Primary" ? "Saving" : "Working");
                    }

                    row.Children.Add(button);
                }

                board.Children.Add(row);
            }

            var fields = new WrapPanel { Margin = new Thickness(0, 12, 0, 16) };
            fields.Children.Add(Field("Search", new TextBox { Width = 240 }, box => { box.SetResourceReference(FrameworkElement.StyleProperty, "TextBox.Search"); InputAssist.SetPlaceholder(box, "Search name or phone"); SearchAssist.SetShortcut(box, "Ctrl F"); }));
            fields.Children.Add(Field("Donor name", new TextBox { Width = 200, Text = "Adrian Mu", Tag = "focus" }, _ => { }));
            fields.Children.Add(new FormField { Label = "Phone", Error = "Enter the full number, like +1-555-0175.", Width = 240, Margin = new Thickness(0, 0, 16, 0), Content = new TextBox { Text = "+1-555-01" } });
            // Read-only keeps ink text on a quiet fill (it can be read and copied); disabled is muted. An error from a FormField gives any input the Critical edge.
            fields.Children.Add(Field("Donor ID (read-only)", new TextBox { Width = 150, Text = "D-0175", IsReadOnly = true }, _ => { }));
            fields.Children.Add(Field("Staff ID (disabled)", new TextBox { Width = 150, Text = "S-0002", IsEnabled = false }, _ => { }));
            fields.Children.Add(new FormField { Label = "Temporary password", Error = "Type the temporary password again.", Width = 220, Margin = new Thickness(0, 0, 16, 0), Content = new PasswordField() });
            fields.Children.Add(Field("Blood group", new ComboBox { Width = 180, ItemsSource = new[] { "All blood groups", "O−" }, SelectedIndex = 0 }, _ => { }));
            board.Children.Add(fields);

            var selection = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
            selection.Children.Add(new CheckBox { Content = "Keep me signed in", IsChecked = true, Margin = new Thickness(0, 0, 20, 0) });
            selection.Children.Add(new CheckBox { Content = "Unchecked", Margin = new Thickness(0, 0, 20, 0) });
            selection.Children.Add(new RadioButton { Content = "Comfortable", IsChecked = true, Margin = new Thickness(0, 0, 12, 0), GroupName = "d" });
            selection.Children.Add(new RadioButton { Content = "Compact", Margin = new Thickness(0, 0, 20, 0), GroupName = "d" });
            var toggle = new CheckBox { Content = "Notifications", IsChecked = true, Margin = new Thickness(0, 0, 20, 0) };
            toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
            selection.Children.Add(toggle);
            var segmented = new ListBox { ItemsSource = new[] { "Light", "Dark", "Match Windows" }, SelectedIndex = 2 };
            segmented.SetResourceReference(FrameworkElement.StyleProperty, "Segmented");
            selection.Children.Add(segmented);
            board.Children.Add(selection);

            var badges = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
            foreach (var group in new[] { "A+", "O−", "AB+" })
            {
                badges.Children.Add(new BloodGroupTag { Text = group, Margin = new Thickness(0, 0, 8, 0) });
            }

            badges.Children.Add(new Badge { Text = "Eligible", Kind = BadgeKind.Success, ShowDot = true, Margin = new Thickness(0, 0, 8, 0) });
            badges.Children.Add(new Badge { Text = "Deferred until 29 Jan", Kind = BadgeKind.Neutral, Margin = new Thickness(0, 0, 8, 0) });
            badges.Children.Add(new Badge { Text = "First-time", Kind = BadgeKind.Info, Margin = new Thickness(0, 0, 8, 0) });
            badges.Children.Add(new Badge { Text = "Pending", Kind = BadgeKind.Info, Icon = IconKind.Clock, Margin = new Thickness(0, 0, 8, 0) });
            badges.Children.Add(new Badge { Text = "Low stock", Kind = BadgeKind.Critical, Icon = IconKind.Alert, Margin = new Thickness(0, 0, 8, 0) });
            badges.Children.Add(new Badge { Text = "Expires in 2 days", Kind = BadgeKind.Warning, Icon = IconKind.Clock, Margin = new Thickness(0, 0, 8, 0) });
            badges.Children.Add(new Avatar { Initials = "AM", IsBlood = true, Margin = new Thickness(8, 0, 8, 0) });
            badges.Children.Add(new Avatar { Initials = "CB", Margin = new Thickness(0, 0, 8, 0) });
            // No real photo (the placeholder drawing of 2.0 is not one): initials. A real photo: the photo.
            badges.Children.Add(new Avatar { Initials = "SS", Source = Lifestream.Desktop.Services.DefaultAvatar.Image, Margin = new Thickness(0, 0, 8, 0) });
            badges.Children.Add(new Avatar { Initials = "HG", Source = Lifestream.Desktop.Services.PhotoDecoder.Decode(Lifestream.Desktop.Tests.Features.DemoData2.Jpeg(), 64), Margin = new Thickness(0, 0, 16, 0) });
            badges.Children.Add(new BloodGroupTag { Text = "O−", IsLarge = true, Margin = new Thickness(0, 0, 16, 0) });
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (tab, on) in new[] { ("All 8", true), ("Low 2", false), ("Expiring 3", false) })
            {
                var option = new RadioButton { Content = tab, IsChecked = on, GroupName = "tabs" };
                option.SetResourceReference(FrameworkElement.StyleProperty, "Tab.Option");
                tabs.Children.Add(option);
            }

            badges.Children.Add(tabs);
            board.Children.Add(badges);

            var lower = new StackPanel { Orientation = Orientation.Horizontal };
            lower.Children.Add(new EmptyState { Title = "No donors match these filters", Message = "No O− donors are eligible before 1 Dec. Clear the filters to see all 40 donors.", ActionText = "Clear filters", ActionCommand = new Lifestream.Client.Common.AsyncRelayCommand(_ => Task.CompletedTask), Width = 420, Margin = new Thickness(0, 0, 24, 0) });
            var progress = new ProgressBar { IsIndeterminate = false, Value = 40, Width = 200, Margin = new Thickness(0, 12, 0, 0) };
            var surfaceText = new StackPanel();
            surfaceText.Children.Add(new TextBlock { Text = "Cancel this appointment?", FontWeight = FontWeights.SemiBold });
            surfaceText.Children.Add(new TextBlock { Text = "Asher Devereux, today at 11:00.", Margin = new Thickness(0, 6, 0, 0) });
            // The inline confirm of a destructive action: the safe choice first, the action named in full.
            var confirm = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var keep = new Button { Content = "Keep appointment", Margin = new Thickness(0, 0, 8, 0) };
            keep.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary.Small");
            var cancelIt = new Button { Content = "Cancel appointment" };
            cancelIt.SetResourceReference(FrameworkElement.StyleProperty, "Button.Danger.Small");
            confirm.Children.Add(keep);
            confirm.Children.Add(cancelIt);
            surfaceText.Children.Add(confirm);
            var right = new StackPanel();
            right.Children.Add(new ElevatedSurface { Level = ElevationLevel.Flyout, Padding = new Thickness(14, 12, 14, 12), Width = 320, Content = surfaceText });
            right.Children.Add(progress);
            lower.Children.Add(right);
            board.Children.Add(lower);

            using var host = Hosted.Create(root, 1400, 760);
            foreach (var b in host.All<Button>())
            {
                switch (b.Tag as string)
                {
                    case "Hover": VisualStateManager.GoToState(b, "MouseOver", false); break;
                    case "Pressed": VisualStateManager.GoToState(b, "Pressed", false); break;
                    case "Focus": FocusAssist.SetShowRing(b, true); break;
                }
            }

            host.Settle(400);
            var png = host.Capture();
            System.IO.Directory.CreateDirectory(RenderTests.Directory);
            File.WriteAllBytes(Path.Combine(RenderTests.Directory, $"components-{theme}.png"), png);
            return host.All<Control>().Count(c => c.Template is not null && VisualTreeHelper.GetChildrenCount(c) > 0);
        });

        Assert.True(count > 40, $"only {count} controls built their template");
    }

    private static StackPanel Field<T>(string label, T input, Action<T> setup)
        where T : Control
    {
        setup(input);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var text = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) };
        text.SetResourceReference(FrameworkElement.StyleProperty, "Text.Label");
        panel.Children.Add(text);
        panel.Children.Add(input);
        return panel;
    }
}
