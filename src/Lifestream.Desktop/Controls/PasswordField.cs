using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// The one masked password control (SHELL-41, SHELL-03): a password box with an optional "Show" toggle. <see cref="Password"/>
/// is a two-way bindable property that always mirrors what the box displays, in both directions, so a cleared view model
/// clears the box. While <see cref="IsReadOnly"/> is true the text cannot be edited. A stored password must never be bound to
/// a visible control: the toggle only reveals what the user typed in this session.
/// </summary>
[TemplatePart(Name = "PART_Password", Type = typeof(PasswordBox))]
[TemplatePart(Name = "PART_Plain", Type = typeof(TextBox))]
[TemplatePart(Name = "PART_Toggle", Type = typeof(System.Windows.Controls.Primitives.ToggleButton))]
public sealed class PasswordField : Control
{
    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.Register(nameof(Password), typeof(string), typeof(PasswordField), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    public static readonly DependencyProperty ShowToggleProperty =
        DependencyProperty.Register(nameof(ShowToggle), typeof(bool), typeof(PasswordField), new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty IsRevealedProperty =
        DependencyProperty.Register(nameof(IsRevealed), typeof(bool), typeof(PasswordField), new FrameworkPropertyMetadata(false, OnRevealChanged));

    public static readonly DependencyProperty IsReadOnlyProperty =
        DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(PasswordField), new FrameworkPropertyMetadata(false));

    private PasswordBox? _box;
    private TextBox? _plain;
    private bool _syncing;

    public string Password { get => (string)GetValue(PasswordProperty); set => SetValue(PasswordProperty, value); }

    public bool ShowToggle { get => (bool)GetValue(ShowToggleProperty); set => SetValue(ShowToggleProperty, value); }

    public bool IsRevealed { get => (bool)GetValue(IsRevealedProperty); set => SetValue(IsRevealedProperty, value); }

    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

    /// <summary>The text the user can currently see (empty when masked): for tests of the "always displays its bound state" rule.</summary>
    public string? DisplayedText => IsRevealed ? _plain?.Text : _box?.Password;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_box is not null)
        {
            _box.PasswordChanged -= OnBoxChanged;
        }

        if (_plain is not null)
        {
            _plain.TextChanged -= OnPlainChanged;
        }

        _box = GetTemplateChild("PART_Password") as PasswordBox;
        _plain = GetTemplateChild("PART_Plain") as TextBox;
        if (_box is not null)
        {
            _box.PasswordChanged += OnBoxChanged;
            _box.PreviewKeyDown += BlockEditsWhenReadOnly;
            _box.PreviewTextInput += (_, e) => e.Handled |= IsReadOnly;
            _box.Password = Password ?? string.Empty;
        }

        if (_plain is not null)
        {
            _plain.TextChanged += OnPlainChanged;
            _plain.Text = Password ?? string.Empty;
        }

        if (GetTemplateChild("PART_Toggle") is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            toggle.Checked += (_, _) => AutomationProperties.SetName(toggle, "Hide password");
            toggle.Unchecked += (_, _) => AutomationProperties.SetName(toggle, "Show password");
        }
    }

    /// <summary>Gives keyboard focus to whichever box is showing.</summary>
    public new bool Focus() => (IsRevealed ? _plain : (UIElement?)_box)?.Focus() ?? false;

    private void BlockEditsWhenReadOnly(object sender, KeyEventArgs e)
    {
        if (IsReadOnly && e.Key is not (Key.Tab or Key.Left or Key.Right or Key.Home or Key.End))
        {
            e.Handled = true;
        }
    }

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (PasswordField)d;
        if (field._syncing)
        {
            return;
        }

        field._syncing = true;
        try
        {
            var text = (string?)e.NewValue ?? string.Empty;
            if (field._box is { } box && box.Password != text)
            {
                box.Password = text;
            }

            if (field._plain is { } plain && plain.Text != text)
            {
                plain.Text = text;
            }
        }
        finally
        {
            field._syncing = false;
        }
    }

    private static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (PasswordField)d;
        if (field._box is { } box && field._plain is { } plain)
        {
            var hadFocus = box.IsKeyboardFocused || plain.IsKeyboardFocused;
            plain.Text = box.Password;
            if (hadFocus)
            {
                (field.IsRevealed ? plain : (UIElement)box).Focus();
            }
        }
    }

    private void OnBoxChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _box is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            Password = _box.Password;
            if (_plain is not null && _plain.Text != _box.Password)
            {
                _plain.Text = _box.Password;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnPlainChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _plain is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            Password = _plain.Text;
            if (_box is not null && _box.Password != _plain.Text)
            {
                _box.Password = _plain.Text;
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}
