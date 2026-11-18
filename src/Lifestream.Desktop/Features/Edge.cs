using System.Windows;
using System.Windows.Controls;

namespace Lifestream.Desktop.Features;

/// <summary>
/// Spacing from the theme's tokens for one side at a time, so no view writes a spacing number (the theme owns every value):
/// <c>feat:Edge.Right="{DynamicResource Space.16}"</c> puts the 16 DIP token to the right of the element, and <c>Edge.PadX</c> and
/// <c>Edge.PadY</c> do the same for padding. A margin or padding that is the same on every side uses the ready-made
/// <c>Pad.*</c> thickness tokens directly instead. The values must be <c>Space.*</c> tokens (<c>DynamicResource</c>, so they follow the theme).
/// </summary>
public static class Edge
{
    public static readonly DependencyProperty LeftProperty = Register("Left", ApplyMargin);
    public static readonly DependencyProperty TopProperty = Register("Top", ApplyMargin);
    public static readonly DependencyProperty RightProperty = Register("Right", ApplyMargin);
    public static readonly DependencyProperty BottomProperty = Register("Bottom", ApplyMargin);
    public static readonly DependencyProperty PadXProperty = Register("PadX", ApplyPadding);
    public static readonly DependencyProperty PadYProperty = Register("PadY", ApplyPadding);

    public static double GetLeft(DependencyObject element) => Get(element, LeftProperty);

    public static void SetLeft(DependencyObject element, double value) => Set(element, LeftProperty, value);

    public static double GetTop(DependencyObject element) => Get(element, TopProperty);

    public static void SetTop(DependencyObject element, double value) => Set(element, TopProperty, value);

    public static double GetRight(DependencyObject element) => Get(element, RightProperty);

    public static void SetRight(DependencyObject element, double value) => Set(element, RightProperty, value);

    public static double GetBottom(DependencyObject element) => Get(element, BottomProperty);

    public static void SetBottom(DependencyObject element, double value) => Set(element, BottomProperty, value);

    public static double GetPadX(DependencyObject element) => Get(element, PadXProperty);

    public static void SetPadX(DependencyObject element, double value) => Set(element, PadXProperty, value);

    public static double GetPadY(DependencyObject element) => Get(element, PadYProperty);

    public static void SetPadY(DependencyObject element, double value) => Set(element, PadYProperty, value);

    private static DependencyProperty Register(string name, PropertyChangedCallback changed) =>
        DependencyProperty.RegisterAttached(name, typeof(double), typeof(Edge), new PropertyMetadata(0d, changed));

    private static double Get(DependencyObject element, DependencyProperty property) => (double)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(property);

    private static void Set(DependencyObject element, DependencyProperty property, double value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(property, value);

    private static void ApplyMargin(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is FrameworkElement target)
        {
            target.Margin = new Thickness(GetLeft(element), GetTop(element), GetRight(element), GetBottom(element));
        }
    }

    private static void ApplyPadding(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        var padding = new Thickness(GetPadX(element), GetPadY(element), GetPadX(element), GetPadY(element));
        switch (element)
        {
            case Border border:
                border.Padding = padding;
                break;
            case Control control:
                control.Padding = padding;
                break;
            case TextBlock text:
                text.Padding = padding;
                break;
        }
    }
}
