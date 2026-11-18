using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Lifestream.Desktop.Chrome;

/// <summary>
/// The one window chrome (SHELL-19), reused by every window (login, main, dialogs). It draws its own caption: edge and corner
/// resizing (6 DIP border), Snap Layouts on the maximize button, double-click maximize and Alt+Space (the system menu), a restore
/// glyph while maximized, per-monitor maximize bounds, and 46x32 caption buttons whose click area equals their highlight
/// because the maximize button's hit test is answered from the button's own bounds.
/// </summary>
[TemplatePart(Name = "PART_Root", Type = typeof(Border))]
[TemplatePart(Name = "PART_Minimize", Type = typeof(Button))]
[TemplatePart(Name = "PART_Maximize", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_Close", Type = typeof(Button))]
public class ChromeWindow : Window
{
    /// <summary>Height of the caption (title) bar and of every caption button.</summary>
    public const double CaptionHeight = 32;

    /// <summary>Width of a caption button.</summary>
    public const double CaptionButtonWidth = 46;

    public static readonly DependencyProperty IsDialogProperty =
        DependencyProperty.Register(nameof(IsDialog), typeof(bool), typeof(ChromeWindow), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsMaximizeHoveredProperty =
        DependencyProperty.Register(nameof(IsMaximizeHovered), typeof(bool), typeof(ChromeWindow), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsMaximizePressedProperty =
        DependencyProperty.Register(nameof(IsMaximizePressed), typeof(bool), typeof(ChromeWindow), new FrameworkPropertyMetadata(false));

    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmNcLeftButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HitMaxButton = 9;

    /// <summary>Opens the system menu (Alt+Space), which the custom caption would otherwise lose.</summary>
    private static readonly RoutedCommand ShowSystemMenu = new(nameof(ShowSystemMenu), typeof(ChromeWindow));

    private FrameworkElement? _maximizeButton;
    private Border? _root;

    public ChromeWindow()
    {
        // The theme (tokens, controls and this window's own template) is shared by every window; it is also merged by the app.
        Resources.MergedDictionaries.Add(Theme.ThemeLoader.Resources);
        Style = (Style)Resources[typeof(ChromeWindow)];
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = CaptionHeight,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
        });
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
        // Alt+Space opens the system menu even though the caption is drawn by us.
        CommandBindings.Add(new CommandBinding(ShowSystemMenu, (_, _) => SystemCommands.ShowSystemMenu(this, PointToScreen(new Point(0, 0)))));
        InputBindings.Add(new KeyBinding(ShowSystemMenu, Key.Space, ModifierKeys.Alt));
        StateChanged += (_, _) => UpdateForState();
    }

    /// <summary>Dialogs have only a close button and cannot be resized or maximized.</summary>
    public bool IsDialog { get => (bool)GetValue(IsDialogProperty); set => SetValue(IsDialogProperty, value); }

    public bool IsMaximizeHovered { get => (bool)GetValue(IsMaximizeHoveredProperty); private set => SetValue(IsMaximizeHoveredProperty, value); }

    public bool IsMaximizePressed { get => (bool)GetValue(IsMaximizePressedProperty); private set => SetValue(IsMaximizePressedProperty, value); }

    /// <summary>The maximize/restore button of the template (for tests and for hit testing).</summary>
    public FrameworkElement? MaximizeButton => _maximizeButton;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _root = GetTemplateChild("PART_Root") as Border;
        _maximizeButton = GetTemplateChild("PART_Maximize") as FrameworkElement;
        UpdateForState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProc);
        }

        if (IsDialog)
        {
            ResizeMode = ResizeMode.NoResize;
        }
    }

    private void UpdateForState()
    {
        if (_root is null)
        {
            return;
        }

        // A maximized window extends past the screen by the resize border; pull the content back inside.
        var border = SystemParameters.WindowResizeBorderThickness;
        _root.Padding = WindowState == WindowState.Maximized ? new Thickness(border.Left, border.Top + 1, border.Right, border.Bottom) : new Thickness(0);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest when _maximizeButton is { IsVisible: true } && !IsDialog:
                if (IsOverMaximize(lParam))
                {
                    // Answering HTMAXBUTTON is what makes Windows 11 show the Snap Layouts flyout.
                    IsMaximizeHovered = true;
                    handled = true;
                    return new IntPtr(HitMaxButton);
                }

                IsMaximizeHovered = false;
                IsMaximizePressed = false;
                break;
            case WmNcLeftButtonDown when wParam.ToInt32() == HitMaxButton:
                IsMaximizePressed = true;
                handled = true;
                break;
            case WmNcLeftButtonUp when wParam.ToInt32() == HitMaxButton:
                IsMaximizePressed = false;
                handled = true;
                if (WindowState == WindowState.Maximized)
                {
                    SystemCommands.RestoreWindow(this);
                }
                else
                {
                    SystemCommands.MaximizeWindow(this);
                }

                break;
            case WmNcMouseLeave:
                IsMaximizeHovered = false;
                IsMaximizePressed = false;
                break;
        }

        return IntPtr.Zero;
    }

    private bool IsOverMaximize(IntPtr lParam)
    {
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        return IsWithinMaximize(_maximizeButton!.PointFromScreen(new Point(x, y)));
    }

    /// <summary>True when a point (in the maximize button's own coordinates) is inside the button: the same rectangle that is highlighted.</summary>
    internal bool IsWithinMaximize(Point local) =>
        _maximizeButton is { } button && local.X >= 0 && local.Y >= 0 && local.X < button.ActualWidth && local.Y < button.ActualHeight;

}
