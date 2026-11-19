using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// Attached properties of the shared Button component (SHELL-05, SHELL-27). <c>IsBusy</c> and <c>BusyText</c> switch a button
/// to its busy state: a spinner replaces the label and the progressive verb is shown, the width does not change, and
/// further clicks are ignored. The Hover and Pressed backgrounds are set by the variant styles from tokens.
/// </summary>
public static class ButtonAssist
{
    public static readonly DependencyProperty IsBusyProperty =
        DependencyProperty.RegisterAttached("IsBusy", typeof(bool), typeof(ButtonAssist), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty BusyTextProperty =
        DependencyProperty.RegisterAttached("BusyText", typeof(string), typeof(ButtonAssist), new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty HoverBackgroundProperty =
        DependencyProperty.RegisterAttached("HoverBackground", typeof(Brush), typeof(ButtonAssist), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty PressedBackgroundProperty =
        DependencyProperty.RegisterAttached("PressedBackground", typeof(Brush), typeof(ButtonAssist), new FrameworkPropertyMetadata(null));

    /// <summary>A 1 px edge drawn over the button (the primary variant's top sheen and bottom shade); null for none.</summary>
    public static readonly DependencyProperty SheenProperty =
        DependencyProperty.RegisterAttached("Sheen", typeof(Brush), typeof(ButtonAssist), new FrameworkPropertyMetadata(null));

    private static readonly DependencyPropertyKey HasLabelPropertyKey =
        DependencyProperty.RegisterAttachedReadOnly("HasLabel", typeof(bool), typeof(ButtonAssist), new FrameworkPropertyMetadata(true));

    /// <summary>
    /// True while the button's content shows text. Set by <see cref="ButtonHost"/> (the root of the button template) each time the button is measured.
    /// An icon-only button (no label, or a label that a narrow window has collapsed) turns busy with the spinner alone, so the busy layer never makes
    /// it wider than its icon.
    /// </summary>
    public static readonly DependencyProperty HasLabelProperty = HasLabelPropertyKey.DependencyProperty;

    static ButtonAssist()
    {
        // A busy button ignores every click, however it was triggered (mouse, Enter, Space, IsDefault).
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnClick), handledEventsToo: true);
    }

    public static bool GetIsBusy(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(IsBusyProperty);

    public static void SetIsBusy(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(IsBusyProperty, value);

    public static bool GetHasLabel(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(HasLabelProperty);

    internal static void SetHasLabel(DependencyObject element, bool value) => element.SetValue(HasLabelPropertyKey, value);

    public static string GetBusyText(DependencyObject element) => (string)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(BusyTextProperty);

    public static void SetBusyText(DependencyObject element, string value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(BusyTextProperty, value);

    public static Brush? GetHoverBackground(DependencyObject element) => (Brush?)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(HoverBackgroundProperty);

    public static void SetHoverBackground(DependencyObject element, Brush? value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(HoverBackgroundProperty, value);

    public static Brush? GetPressedBackground(DependencyObject element) => (Brush?)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(PressedBackgroundProperty);

    public static void SetPressedBackground(DependencyObject element, Brush? value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(PressedBackgroundProperty, value);

    public static Brush? GetSheen(DependencyObject element) => (Brush?)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(SheenProperty);

    public static void SetSheen(DependencyObject element, Brush? value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(SheenProperty, value);

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        // Click is raised after the command ran; a command-bound button is already guarded by CanExecute. For a plain
        // click handler the view model must check IsBusy: this hook only marks the event handled so no parent reacts to it.
        if (sender is DependencyObject d && GetIsBusy(d))
        {
            e.Handled = true;
        }
    }
}

/// <summary>
/// The keyboard focus ring (SHELL-34): a 2 px ring at 3:1 contrast, shown only when focus arrived by keyboard. Templates
/// bind a ring element's visibility to <see cref="ShowRingProperty"/>.
/// </summary>
public static class FocusAssist
{
    public static readonly DependencyProperty ShowRingProperty =
        DependencyProperty.RegisterAttached("ShowRing", typeof(bool), typeof(FocusAssist), new FrameworkPropertyMetadata(false));

    private static bool _registered;

    /// <summary>True when the last input was from the keyboard, so a focus change should show the ring.</summary>
    public static bool LastInputWasKeyboard { get; set; }

    public static bool GetShowRing(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(ShowRingProperty);

    public static void SetShowRing(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(ShowRingProperty, value);

    /// <summary>Hooks input tracking and the focus handlers once per process.</summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(typeof(Control), UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, _) => Update(s)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Control), UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, _) => Update(s)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, _) => LastInputWasKeyboard = true), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => LastInputWasKeyboard = false), handledEventsToo: true);
    }

    /// <summary>The ring is for keyboard users only: it needs focus and keyboard as the last input.</summary>
    public static bool ShouldShowRing(bool hasKeyboardFocus, bool lastInputWasKeyboard) => hasKeyboardFocus && lastInputWasKeyboard;

    private static void Update(object sender)
    {
        if (sender is Control control)
        {
            SetShowRing(control, ShouldShowRing(control.IsKeyboardFocused, LastInputWasKeyboard));
        }
    }
}

/// <summary>
/// The root of the button template: a Grid that also tells the button whether its content shows text (<see cref="ButtonAssist.HasLabelProperty"/>).
/// The label layer and the busy layer share its one cell and are both measured, so a button keeps its width when it turns busy; for a button that
/// shows only an icon the template then drops the busy text, and the button stays as wide as its icon.
/// </summary>
public sealed class ButtonHost : Grid
{
    /// <inheritdoc />
    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(constraint);
        if (TemplatedParent is not { } button || InternalChildren.OfType<ContentPresenter>().FirstOrDefault() is not { } content)
        {
            return size;
        }

        var hasLabel = ShowsText(content);
        if (ButtonAssist.GetHasLabel(button) == hasLabel)
        {
            return size;
        }

        // The template's trigger shows or collapses the busy text at once; measure again with it.
        ButtonAssist.SetHasLabel(button, hasLabel);
        return base.MeasureOverride(constraint);
    }

    /// <summary>
    /// True when a text element under <paramref name="node"/> takes up room. Collapsed text does not count (a label folded away in a narrow window); hidden
    /// text does (the label of a busy button is hidden, not gone). Icons are drawn shapes, and an <see cref="IconText"/> glyph is an icon too.
    /// </summary>
    private static bool ShowsText(DependencyObject node)
    {
        if (node is UIElement element)
        {
            if (element.Visibility == Visibility.Collapsed || element is IconText)
            {
                return false;
            }

            if (element is TextBlock or AccessText)
            {
                return element.DesiredSize.Width > 0;
            }
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (ShowsText(VisualTreeHelper.GetChild(node, i)))
            {
                return true;
            }
        }

        return false;
    }
}
