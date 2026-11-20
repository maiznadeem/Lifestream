using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// Real-app walk, defect 8: the Theme item of the account menu reported "expanded" and nothing opened, because the one menu item template of the
/// app had no submenu popup. Every menu item with items of its own must have the popup WPF opens (PART_Popup) with the items in it, and a chevron.
/// </summary>
public sealed class MenuSubmenuTests
{
    [Fact]
    public void MenuItemTemplate_HasTheSubmenuPopup_WithTheItemsInIt_AndAChevron()
    {
        var f = StaHost.Run(() =>
        {
            var style = (Style)ThemeLoader.Resources["MenuItem.Lifestream"];
            var theme = new MenuItem { Style = style, Header = "Theme" };
            theme.Items.Add(new MenuItem { Style = style, Header = "Light" });
            theme.Items.Add(new MenuItem { Style = style, Header = "Dark" });
            using var host = Hosted.Create(new StackPanel { Children = { theme } }, 300, 200);
            theme.ApplyTemplate();
            var popup = theme.Template.FindName("PART_Popup", theme) as Popup;
            var items = theme.Template.FindName("SubmenuItems", theme) as ItemsPresenter;
            var chevron = theme.Template.FindName("SubmenuChevron", theme) as Icon;
            var inside = false;
            for (DependencyObject? e = items; e is not null; e = LogicalTreeHelper.GetParent(e) ?? System.Windows.Media.VisualTreeHelper.GetParent(e))
            {
                inside |= ReferenceEquals(e, popup);
            }

            return (
                HasPopup: popup is not null,
                OpensWithTheItem: popup is null ? null : BindingOperations.GetBinding(popup, Popup.IsOpenProperty)?.Path.Path,
                ItemsInPopup: items is not null && inside,
                Chevron: chevron?.Kind,
                Closed: popup is { IsOpen: false });
        });

        Assert.True(f.HasPopup, "the menu item template has no PART_Popup: a submenu can never open"); // released 2.1.0 and 2.1.1: no popup
        Assert.Equal("IsSubmenuOpen", f.OpensWithTheItem);
        Assert.True(f.ItemsInPopup, "the submenu's items are not inside the popup");
        Assert.Equal(IconKind.Chevr, f.Chevron);
        Assert.True(f.Closed);
    }
}
