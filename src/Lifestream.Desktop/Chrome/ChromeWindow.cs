using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Chrome;

/// <summary>
/// The one window chrome (SHELL-19), reused by every window (login, main, dialogs). It draws its own 44 DIP title bar (direction A): edge and
/// corner resizing (6 DIP border), Snap Layouts on the maximize button, double-click maximize and Alt+Space (the system menu), a restore glyph
/// while maximized, maximized insets from the real frame thickness (nothing is clipped), and 46 x 44 caption buttons whose click area
/// equals their highlight because the maximize button's hit test is answered from the button's own bounds. On Windows 11 the frame gets Mica
/// (the Brush.Mica fallback paints it elsewhere), DWM's round corners and a dark frame in the dark theme. A window either shows the default
/// caption (mark and title) or, with <see cref="ExtendsContentIntoTitleBar"/>, lets its content draw the title bar (the main window's search
/// and account, the sign-in screen) with only the caption buttons on top.
/// <para>
/// The window keeps its caption and system-menu styles (the system menu, Snap and the taskbar depend on them), so Windows still has caption buttons
/// of its own for it. They are drawn in the top part of the frame and show through wherever the window is transparent there, which with Mica is the
/// whole title bar. So the frame is never extended from the top: for Mica it is extended from the bottom over the whole window
/// (<c>Chrome.GlassFrameMica</c>), which leaves Windows no title bar to draw. Only <c>tests/.../RealWindows</c> can see this: an off-screen render
/// has no window.
/// </para>
/// <para>
/// A transparent window (<see cref="Window.AllowsTransparency"/>: the dialogs, which draw their own surface and shadow) asks Windows for no frame
/// at all: no round corners, no border and no extended frame, or Windows outlines the whole window rectangle, shadow room included.
/// </para>
/// </summary>
[TemplatePart(Name = "PART_Root", Type = typeof(Border))]
[TemplatePart(Name = "PART_Minimize", Type = typeof(Button))]
[TemplatePart(Name = "PART_Maximize", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_Close", Type = typeof(Button))]
public class ChromeWindow : Window
{
    /// <summary>Height of the title bar and of every caption button.</summary>
    public const double CaptionHeight = 44;

    /// <summary>Width of a caption button.</summary>
    public const double CaptionButtonWidth = 46;

    /// <summary>Width of the three caption buttons together: content that extends into the title bar keeps this much free on the right.</summary>
    public const double CaptionButtonsWidth = 3 * CaptionButtonWidth;

    public static readonly DependencyProperty ExtendsContentIntoTitleBarProperty =
        DependencyProperty.Register(nameof(ExtendsContentIntoTitleBar), typeof(bool), typeof(ChromeWindow), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty TitleBarHeightProperty =
        DependencyProperty.Register(nameof(TitleBarHeight), typeof(double), typeof(ChromeWindow), new FrameworkPropertyMetadata(CaptionHeight, (d, _) => ((ChromeWindow)d).OnTitleBarHeightChanged()));

    private static readonly DependencyPropertyKey IsMicaActivePropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsMicaActive), typeof(bool), typeof(ChromeWindow), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsMicaActiveProperty = IsMicaActivePropertyKey.DependencyProperty;

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
    private const int HitClient = 1;
    private const int HitMaxButton = 9;

    /// <summary>Opens the system menu (Alt+Space), which the custom caption would otherwise lose.</summary>
    private static readonly RoutedCommand ShowSystemMenu = new(nameof(ShowSystemMenu), typeof(ChromeWindow));

    private readonly WindowChrome _chrome;
    private FrameworkElement? _maximizeButton;
    private Border? _root;
    private bool _laidOutMaximized;

    public ChromeWindow()
    {
        // The theme (tokens, controls and this window's own template) is shared by every window; it is also merged by the app.
        Resources.MergedDictionaries.Add(Theme.ThemeLoader.Resources);
        Style = (Style)Resources[typeof(ChromeWindow)];
        WindowStyle = WindowStyle.SingleBorderWindow;
        _chrome = new WindowChrome
        {
            CaptionHeight = CaptionHeight,
            ResizeBorderThickness = ThemeTokens.Inset("Chrome.ResizeBorder"),
            GlassFrameThickness = ThemeTokens.Inset("Chrome.GlassFrame"),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
        };
        WindowChrome.SetWindowChrome(this, _chrome);
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
        // Alt+Space opens the system menu even though the caption is drawn by us.
        CommandBindings.Add(new CommandBinding(ShowSystemMenu, (_, _) => SystemCommands.ShowSystemMenu(this, PointToScreen(new Point(0, 0)))));
        InputBindings.Add(new KeyBinding(ShowSystemMenu, Key.Space, ModifierKeys.Alt));
        StateChanged += (_, _) => UpdateForState();
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;
    }

    /// <summary>The content draws the title bar itself; the window only adds the caption buttons on top, at the right.</summary>
    public bool ExtendsContentIntoTitleBar { get => (bool)GetValue(ExtendsContentIntoTitleBarProperty); set => SetValue(ExtendsContentIntoTitleBarProperty, value); }

    /// <summary>Height of the title bar, its drag area and the caption buttons: 44, or 40 in a compact window.</summary>
    public double TitleBarHeight { get => (double)GetValue(TitleBarHeightProperty); set => SetValue(TitleBarHeightProperty, value); }

    /// <summary>True when Windows draws Mica behind the window (Windows 11); the Brush.Mica fallback is then transparent in this window.</summary>
    public bool IsMicaActive { get => (bool)GetValue(IsMicaActiveProperty); private set => SetValue(IsMicaActivePropertyKey, value); }

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
        if (AllowsTransparency)
        {
            // Before the window chrome sees the window: a frame it has extended once stays extended.
            _chrome.GlassFrameThickness = new Thickness(0);
        }

        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProc);
            if (AllowsTransparency)
            {
                Dwm.SetNoFrame(source.Handle);
            }
            else
            {
                Dwm.SetRoundCorners(source.Handle);
            }

            ApplyFrame(source);
        }

        if (IsDialog)
        {
            ResizeMode = ResizeMode.NoResize;
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyStateLayout(_laidOutMaximized);
    }

    private void UpdateForState() => ApplyStateLayout(WindowState == WindowState.Maximized);

    /// <summary>
    /// Lays the window out as restored or as maximized. Separate from <see cref="Window.WindowState"/> so the maximized layout can be checked in a
    /// window that is not on a monitor (tests/.../RealWindows): the same code runs when the state really changes.
    /// </summary>
    internal void ApplyStateLayout(bool maximized)
    {
        _laidOutMaximized = maximized;

        // A maximized window extends past the screen by the resize frame plus the padded border on every side; pull the content back inside by
        // exactly that, so no edge is clipped and nothing is left over.
        var inset = maximized ? Dwm.MaximizedOverhang(VisualTreeHelper.GetDpi(this).PixelsPerInchX) : new Thickness(0);

        // The drag area is measured from the window's own top edge, which is off-screen by the inset while maximized.
        _chrome.CaptionHeight = TitleBarHeight + inset.Top;
        if (_root is null)
        {
            return;
        }

        _root.Padding = inset;
        // Windows 11 draws the frame's 1 px border and round corners itself; Windows 10 gets a 1 px stroke from us while restored. A transparent
        // window draws its own surface, so it never gets one.
        _root.BorderThickness = maximized || AllowsTransparency || Dwm.DrawsRoundCorners ? new Thickness(0) : ThemeTokens.Inset("Pad.1");
    }

    /// <summary>
    /// How far a maximized window reaches past the screen on each side (the resize frame plus the padded border), at the system's DPI. A window
    /// uses the value for its own monitor's DPI.
    /// </summary>
    public static Thickness MaximizedInset => Dwm.MaximizedOverhang(Dwm.SystemDpi);

    private void OnTitleBarHeightChanged() => ApplyStateLayout(_laidOutMaximized);

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            ApplyFrame(source);
        }
    }

    /// <summary>Mica, the dark frame and the window background, from the current theme.</summary>
    private void ApplyFrame(HwndSource source)
    {
        Dwm.SetDarkFrame(source.Handle, ThemeService.IsDark);
        if (IsDialog || AllowsTransparency || IsMicaActive)
        {
            return;
        }

        if (Dwm.SetMica(source.Handle))
        {
            // Mica shows through wherever the window paints Brush.Mica: the window surface and this window's copy of the brush become transparent,
            // and the frame is extended under all of it, from the bottom (never from the top: see the class remarks).
            IsMicaActive = true;
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            _chrome.GlassFrameThickness = ThemeTokens.Inset("Chrome.GlassFrameMica");
            Resources["Brush.Mica"] = Brushes.Transparent;
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                if (_maximizeButton is { IsVisible: true } && !IsDialog)
                {
                    if (IsOverMaximize(lParam))
                    {
                        // Answering HTMAXBUTTON is what makes Windows 11 show the Snap Layouts flyout.
                        IsMaximizeHovered = true;
                        handled = true;
                        return new IntPtr(HitMaxButton);
                    }

                    IsMaximizeHovered = false;
                    IsMaximizePressed = false;
                }

                if (IsJustUnderTitleBar(lParam))
                {
                    // The window chrome adds its resize border to the height of the drag area, so the first rows of the content under the
                    // title bar would drag the window (and not click). The drag area is the title bar and nothing more.
                    handled = true;
                    return new IntPtr(HitClient);
                }

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

    private bool IsOverMaximize(IntPtr lParam) => IsWithinMaximize(_maximizeButton!.PointFromScreen(ScreenPoint(lParam)));

    /// <summary>True for a point in the rows right under the title bar that the window chrome would still treat as the drag area (not on the left or right resize border).</summary>
    private bool IsJustUnderTitleBar(IntPtr lParam)
    {
        var point = PointFromScreen(ScreenPoint(lParam));
        var border = _chrome.ResizeBorderThickness;
        return point.Y >= _chrome.CaptionHeight && point.Y < _chrome.CaptionHeight + border.Top && point.X >= border.Left && point.X < ActualWidth - border.Right;
    }

    private static Point ScreenPoint(IntPtr lParam) => new((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));

    /// <summary>True when a point (in the maximize button's own coordinates) is inside the button: the same rectangle that is highlighted.</summary>
    internal bool IsWithinMaximize(Point local) =>
        _maximizeButton is { } button && local.X >= 0 && local.Y >= 0 && local.X < button.ActualWidth && local.Y < button.ActualHeight;

}
