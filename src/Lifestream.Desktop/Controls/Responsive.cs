using System.Windows;

namespace Lifestream.Desktop.Controls;

/// <summary>The width classes of the main window (docs/design-system.md, "Breakpoints").</summary>
public enum Breakpoint
{
    /// <summary>Below 1280 DIP: the sidebar is an icon rail, the side panel is 352 DIP, dashboard rows stack, filter chips fold into one button.</summary>
    Compact,

    /// <summary>1280 to 1599 DIP: the full layout.</summary>
    Standard,

    /// <summary>1600 DIP and wider: wide tables may show extra columns; reading layouts keep their maximum width.</summary>
    Wide,
}

/// <summary>
/// Breakpoints for any layout. Set <c>Responsive.Source="True"</c> on the element whose width decides (the shell's root); it then sets the inherited
/// <see cref="BreakpointProperty"/> on itself, so every element inside (every page) can read it with a trigger:
/// <code>&lt;DataTrigger Binding="{Binding (controls:Responsive.Breakpoint), RelativeSource={RelativeSource Self}}" Value="Compact"&gt;</code>
/// </summary>
public static class Responsive
{
    /// <summary>The window width (DIP) where Standard starts.</summary>
    public const double StandardWidth = 1280;

    /// <summary>The window width (DIP) where Wide starts.</summary>
    public const double WideWidth = 1600;

    public static readonly DependencyProperty BreakpointProperty =
        DependencyProperty.RegisterAttached("Breakpoint", typeof(Breakpoint), typeof(Responsive), new FrameworkPropertyMetadata(Breakpoint.Standard, FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached("Source", typeof(bool), typeof(Responsive), new FrameworkPropertyMetadata(false, OnSourceChanged));

    public static Breakpoint GetBreakpoint(DependencyObject element) => (Breakpoint)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(BreakpointProperty);

    public static void SetBreakpoint(DependencyObject element, Breakpoint value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(BreakpointProperty, value);

    public static bool GetSource(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(SourceProperty, value);

    /// <summary>The breakpoint of a width in DIP.</summary>
    public static Breakpoint For(double width) => width >= WideWidth ? Breakpoint.Wide : width >= StandardWidth ? Breakpoint.Standard : Breakpoint.Compact;

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnSizeChanged;
        if ((bool)e.NewValue)
        {
            element.SizeChanged += OnSizeChanged;
            Update(element);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

    private static void Update(FrameworkElement element)
    {
        if (element.ActualWidth <= 0)
        {
            return;
        }

        var breakpoint = For(element.ActualWidth);
        if (GetBreakpoint(element) != breakpoint)
        {
            SetBreakpoint(element, breakpoint);
        }
    }
}
