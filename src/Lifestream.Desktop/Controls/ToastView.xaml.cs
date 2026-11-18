using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Lifestream.Client.Common;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// One toast (SHELL-30): the kind sets the icon and accent from tokens; hovering or focusing it pauses its countdown; its message
/// is a polite live region, so a screen reader announces it when it appears.
/// </summary>
public partial class ToastView : UserControl
{
    public ToastView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplyKind();
        Loaded += (_, _) =>
        {
            ApplyKind();
            UIElementAutomationPeer.CreatePeerForElement(MessageText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        };
        MouseEnter += (_, _) => Item?.Pause();
        MouseLeave += (_, _) => ResumeIfIdle();
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                Item?.Pause();
            }
            else
            {
                ResumeIfIdle();
            }
        };
    }

    private ToastItem? Item => DataContext as ToastItem;

    private void ResumeIfIdle()
    {
        if (!IsMouseOver && !IsKeyboardFocusWithin)
        {
            Item?.Resume();
        }
    }

    private void ApplyKind()
    {
        if (Item is null)
        {
            return;
        }

        var (glyph, brushKey) = Item.Kind switch
        {
            ToastKind.Success => ("", "Brush.StatusSuccess"),
            ToastKind.Warning => ("", "Brush.StatusWarning"),
            ToastKind.Error => ("", "Brush.StatusDanger"),
            _ => ("", "Brush.StatusInfo"),
        };
        Icon.Text = glyph;
        if (TryFindResource(brushKey) is System.Windows.Media.Brush brush)
        {
            Icon.Foreground = brush;
            Accent.Background = brush;
        }

        AutomationProperties.SetName(this, $"{Item.Kind} notification");
    }
}

/// <summary>The bottom-right stack of toasts. Bind its DataContext to the toast service.</summary>
public sealed class ToastHost : ItemsControl
{
    public ToastHost()
    {
        Focusable = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 24, 24);
        AutomationProperties.SetName(this, "Notifications");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        ((INotifyCollectionChanged)Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
