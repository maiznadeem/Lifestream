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
/// The keyboard focus ring (SHELL-34): the design system's 2 px ring, shown only while the person is moving through the app with the keyboard.
/// Templates bind a ring element's visibility to <see cref="ShowRingProperty"/>; no template reads <c>IsKeyboardFocused</c> for it, because that is
/// also true after a mouse click.
/// <para>
/// The rule, the same as a browser's <c>:focus-visible</c>:
/// a key that moves or acts (Tab, the arrows, Space, Enter, a letter) turns keyboard mode on; a modifier or system key on its own (Shift, Ctrl, Alt,
/// the Windows key, Print Screen, Caps Lock, a key pressed with Alt or the Windows key held) does not, so Alt+Tab or Win+Shift+S never leaves a ring
/// behind; a press of the mouse, a pen or a finger turns keyboard mode off and removes the ring that is showing, also when the focus does not move
/// (a click on the control that already has it).
/// </para>
/// </summary>
public static class FocusAssist
{
    public static readonly DependencyProperty ShowRingProperty =
        DependencyProperty.RegisterAttached("ShowRing", typeof(bool), typeof(FocusAssist), new FrameworkPropertyMetadata(false));

    private static bool _registered;

    /// <summary>The control whose ring is showing: a pointer press takes the ring off it even though it keeps the focus.</summary>
    [ThreadStatic]
    private static WeakReference<DependencyObject>? _ringOwner;

    /// <summary>True while the person is using the keyboard to move through the app, so a focus change should show the ring.</summary>
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
        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) => OnKeyDown(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => OnPointerDown()), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewStylusDownEvent, new StylusDownEventHandler((_, _) => OnPointerDown()), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>((_, _) => OnPointerDown()), handledEventsToo: true);
    }

    /// <summary>The ring is for keyboard users only: it needs focus and keyboard as the last input.</summary>
    public static bool ShouldShowRing(bool hasKeyboardFocus, bool lastInputWasKeyboard) => hasKeyboardFocus && lastInputWasKeyboard;

    /// <summary>
    /// True for a key press that means "I am using the keyboard here". A modifier or system key on its own does not, nor does a key pressed with Alt
    /// or the Windows key held: those belong to Windows (Alt+Tab, Win+Shift+S) or to a mouse gesture (Ctrl+click, Shift+click).
    /// </summary>
    public static bool IsKeyboardUse(Key key, ModifierKeys modifiers) =>
        (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0
        && key is not (Key.None or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
            or Key.System or Key.CapsLock or Key.NumLock or Key.Scroll or Key.PrintScreen or Key.Apps or Key.Sleep or Key.DeadCharProcessed or Key.ImeProcessed
            or Key.VolumeDown or Key.VolumeUp or Key.VolumeMute or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaPlayPause or Key.MediaStop);

    /// <summary>A key went down somewhere in the app.</summary>
    internal static void OnKeyDown(Key key, ModifierKeys modifiers)
    {
        if (IsKeyboardUse(key, modifiers))
        {
            LastInputWasKeyboard = true;
        }
    }

    /// <summary>The mouse, a pen or a finger went down somewhere in the app: the keyboard is not in use, and the ring that is showing goes.</summary>
    internal static void OnPointerDown()
    {
        LastInputWasKeyboard = false;
        if (_ringOwner is not null && _ringOwner.TryGetTarget(out var owner))
        {
            SetShowRing(owner, false);
        }

        _ringOwner = null;
    }

    private static void Update(object sender)
    {
        if (sender is Control control)
        {
            Apply(control, control.IsKeyboardFocused);
        }
    }

    /// <summary>Shows or hides the ring of a control for its focus state (the focus handlers call it; tests call it with the state they want).</summary>
    internal static void Apply(DependencyObject control, bool hasKeyboardFocus)
    {
        var show = ShouldShowRing(hasKeyboardFocus, LastInputWasKeyboard);
        SetShowRing(control, show);
        if (show)
        {
            _ringOwner = new WeakReference<DependencyObject>(control);
        }
        else if (_ringOwner is not null && _ringOwner.TryGetTarget(out var owner) && ReferenceEquals(owner, control))
        {
            _ringOwner = null;
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
