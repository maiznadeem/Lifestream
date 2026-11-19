using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// The Lifestream mark of direction A (a blood drop with a white stream wave), drawn from vector geometry at any size in the theme's blood colour.
/// Its template is in Chrome/Chrome.xaml. It is decoration: the wordmark next to it is live text, so screen readers skip the mark.
/// </summary>
public sealed class LogoMark : Control
{
    static LogoMark() => DefaultStyleKeyProperty.OverrideMetadata(typeof(LogoMark), new FrameworkPropertyMetadata(typeof(LogoMark)));

    protected override AutomationPeer OnCreateAutomationPeer() => new LogoPeer(this);

    private sealed class LogoPeer(LogoMark owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override string GetClassNameCore() => nameof(LogoMark);
    }
}
