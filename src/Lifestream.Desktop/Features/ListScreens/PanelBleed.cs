using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// Lets a page's side panel reach the window's edges as in direction A (from under the title bar to the bottom, flush right), although the shell pads the page:
/// set <c>lists:PanelBleed.IsEnabled="True"</c> on the panel and its margin becomes the negative of the page host's padding on the top, right and bottom. It
/// follows the host when the shell changes its padding (a compact window). Outside the shell (a test host) it does nothing.
/// </summary>
public static class PanelBleed
{
    /// <summary>The name of the shell's page host (Shell/ShellView.xaml).</summary>
    public const string HostName = "PageHost";

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(PanelBleed), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty HostProperty =
        DependencyProperty.RegisterAttached("Host", typeof(FrameworkElement), typeof(PanelBleed), new PropertyMetadata(null));

    private static readonly DependencyPropertyDescriptor MarginDescriptor = DependencyPropertyDescriptor.FromProperty(FrameworkElement.MarginProperty, typeof(FrameworkElement));

    public static bool GetIsEnabled(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        if ((bool)e.NewValue)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            if (element.IsLoaded)
            {
                Attach(element);
            }
        }
        else
        {
            Detach(element);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((FrameworkElement)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Detach((FrameworkElement)sender);

    private static void Attach(FrameworkElement element)
    {
        Detach(element);
        var host = FindHost(element);
        if (host is null)
        {
            return;
        }

        element.SetValue(HostProperty, host);
        EventHandler changed = (_, _) => Update(element, host);
        element.SetValue(HandlerProperty, changed);
        MarginDescriptor.AddValueChanged(host, changed);
        Update(element, host);
    }

    private static readonly DependencyProperty HandlerProperty =
        DependencyProperty.RegisterAttached("Handler", typeof(EventHandler), typeof(PanelBleed), new PropertyMetadata(null));

    private static void Detach(FrameworkElement element)
    {
        if (element.GetValue(HostProperty) is FrameworkElement host && element.GetValue(HandlerProperty) is EventHandler changed)
        {
            MarginDescriptor.RemoveValueChanged(host, changed);
        }

        element.ClearValue(HostProperty);
        element.ClearValue(HandlerProperty);
    }

    private static void Update(FrameworkElement element, FrameworkElement host)
    {
        var pad = host.Margin;
        element.Margin = new Thickness(0, -pad.Top, -pad.Right, -pad.Bottom);
    }

    private static FrameworkElement? FindHost(DependencyObject start)
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Name: HostName } host)
            {
                return host;
            }
        }

        return null;
    }
}
