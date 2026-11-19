using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Controls;

/// <summary>The colour family of a <see cref="Badge"/>: a soft background in the status colour, or a quiet bordered neutral.</summary>
public enum BadgeKind
{
    /// <summary>Layer2 fill, Ink2 text, 1 px Stroke edge.</summary>
    Neutral,

    /// <summary>SuccessSoft fill, Success text.</summary>
    Success,

    /// <summary>WarningSoft fill, Warning text.</summary>
    Warning,

    /// <summary>CriticalSoft fill, Critical text.</summary>
    Critical,

    /// <summary>InfoSoft fill, Info text.</summary>
    Info,
}

/// <summary>How high a surface floats, which sets its shadow: flyouts and toasts, dialogs and the palette, or a side panel.</summary>
public enum ElevationLevel
{
    /// <summary>Menus, flyouts and toasts: blur 16, offset 8, 14 %.</summary>
    Flyout,

    /// <summary>Dialogs and the command palette: blur 64, offset 32, 22 %.</summary>
    Dialog,

    /// <summary>A side panel: blur 24, no offset, 10 %.</summary>
    Panel,
}

/// <summary>Shared helpers of the components in this file.</summary>
internal static class ComponentHelpers
{
    /// <summary>Runs <paramref name="command"/> when it can run, or <paramref name="fallback"/> when there is no command.</summary>
    internal static void RunClose(ICommand? command, Action fallback)
    {
        if (command is null)
        {
            fallback();
        }
        else if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    /// <summary>The name for UI Automation: the text, or nothing when it is empty (so an explicit AutomationProperties.Name in XAML is not wiped by an empty binding).</summary>
    internal static void SetName(DependencyObject element, string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            AutomationProperties.SetName(element, name);
        }
    }
}

/// <summary>An automation peer that reports a given control type (Group, Pane, Text, Image) and class name for a component.</summary>
internal sealed class ComponentAutomationPeer(FrameworkElement owner, AutomationControlType controlType, string className) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => controlType;

    protected override string GetClassNameCore() => className;
}

/// <summary>
/// A page section panel: a Layer card with a 1 px Stroke and rounded corners, a header row (heading, muted count, actions on the right) and the content below,
/// clipped to the rounded corners.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:Section Header="Upcoming appointments" Subtitle="4 items"&gt;
///   &lt;controls:Section.HeaderActions&gt;&lt;Button Style="{StaticResource Button.Subtle.Small}" Content="View all" /&gt;&lt;/controls:Section.HeaderActions&gt;
///   &lt;ListBox ... /&gt;
/// &lt;/controls:Section&gt;
/// </code>
/// </example>
public sealed class Section : ContentControl
{
    /// <summary>The heading (15 SemiBold). Also the automation name of the section.</summary>
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(Section), new FrameworkPropertyMetadata(string.Empty, (d, e) => ComponentHelpers.SetName(d, e.NewValue as string)));

    /// <summary>The muted text after the heading, such as "4 items".</summary>
    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(Section), new FrameworkPropertyMetadata(null));

    /// <summary>Content right-aligned in the header row (buttons, a segmented control).</summary>
    public static readonly DependencyProperty HeaderActionsProperty =
        DependencyProperty.Register(nameof(HeaderActions), typeof(object), typeof(Section), new FrameworkPropertyMetadata(null));

    private FrameworkElement? _clipHost;

    /// <summary>The heading text.</summary>
    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <summary>The muted count or caption after the heading; the part is hidden when empty.</summary>
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    /// <summary>Content shown at the right end of the header row; the part is hidden when null.</summary>
    public object? HeaderActions { get => GetValue(HeaderActionsProperty); set => SetValue(HeaderActionsProperty, value); }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_clipHost is not null)
        {
            _clipHost.SizeChanged -= OnClipHostSizeChanged;
        }

        _clipHost = GetTemplateChild("PART_ClipHost") as FrameworkElement;
        if (_clipHost is not null)
        {
            _clipHost.SizeChanged += OnClipHostSizeChanged;
            UpdateClip();
        }
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new ComponentAutomationPeer(this, AutomationControlType.Group, nameof(Section));

    private void OnClipHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdateClip();

    /// <summary>
    /// Clips the content area to the panel's rounded bottom corners (the radius inside the 1 px edge). The clip reaches above the area, so the
    /// rounded top corners lie outside it and only the bottom corners cut.
    /// </summary>
    private void UpdateClip()
    {
        if (_clipHost is not { ActualWidth: > 0, ActualHeight: > 0 } host)
        {
            return;
        }

        var radius = Math.Max(0, ThemeTokens.Radius("Radius.Panel").BottomLeft - 1);
        var overshoot = radius * 2;
        var clip = new RectangleGeometry(new Rect(0, -overshoot, host.ActualWidth, host.ActualHeight + overshoot), radius, radius);
        clip.Freeze();
        host.Clip = clip;
    }
}

/// <summary>
/// The top of a page: an overline (the date), the title with an optional count badge, actions on the right (bottom-aligned with the title) and a
/// full-width tab row below. Empty parts take no space.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:PageHeader Overline="Tuesday, 11 November" Title="Donors" Count="40"&gt;
///   &lt;controls:PageHeader.Actions&gt;&lt;Button Style="{StaticResource Button.Primary}" Content="Add donor" /&gt;&lt;/controls:PageHeader.Actions&gt;
/// &lt;/controls:PageHeader&gt;
/// </code>
/// </example>
public sealed class PageHeader : Control
{
    /// <summary>The 13 px muted line above the title.</summary>
    public static readonly DependencyProperty OverlineProperty =
        DependencyProperty.Register(nameof(Overline), typeof(string), typeof(PageHeader), new FrameworkPropertyMetadata(null));

    /// <summary>The page title (28/36 SemiBold, a level 1 heading for screen readers).</summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new FrameworkPropertyMetadata(string.Empty));

    /// <summary>The text of the neutral badge right of the title (a total, "40"); the badge is hidden when empty.</summary>
    public static readonly DependencyProperty CountProperty =
        DependencyProperty.Register(nameof(Count), typeof(string), typeof(PageHeader), new FrameworkPropertyMetadata(null));

    /// <summary>Buttons at the right end of the title row.</summary>
    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader), new FrameworkPropertyMetadata(null));

    /// <summary>A full-width row below the title (an underline tab strip), 16 px under it.</summary>
    public static readonly DependencyProperty TabsProperty =
        DependencyProperty.Register(nameof(Tabs), typeof(object), typeof(PageHeader), new FrameworkPropertyMetadata(null));

    /// <summary>
    /// The load state of the page. While it is Loading, Waking or Error the count is hidden (there is no true number to show). The default style reads
    /// it from the <c>State</c> of the view model the header is bound to, so a list screen need not set it.
    /// </summary>
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(Lifestream.Client.Common.LoadState), typeof(PageHeader), new FrameworkPropertyMetadata(Lifestream.Client.Common.LoadState.Ready));

    /// <summary>The page's load state.</summary>
    public Lifestream.Client.Common.LoadState State { get => (Lifestream.Client.Common.LoadState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    /// <summary>The line above the title.</summary>
    public string? Overline { get => (string?)GetValue(OverlineProperty); set => SetValue(OverlineProperty, value); }

    /// <summary>The page title.</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>The count shown in a neutral badge next to the title.</summary>
    public string? Count { get => (string?)GetValue(CountProperty); set => SetValue(CountProperty, value); }

    /// <summary>Content right-aligned in the title row.</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    /// <summary>Content of the row below the title.</summary>
    public object? Tabs { get => GetValue(TabsProperty); set => SetValue(TabsProperty, value); }
}

/// <summary>
/// A small label chip: height 22, radius 4, 12 Medium text, with an optional 6 px dot or 13 px icon before the text. The colour family is
/// <see cref="Kind"/>; meaning must never rest on colour alone, so give a status an icon or a dot as well as its words.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:Badge Text="Eligible" Kind="Success" ShowDot="True" /&gt;
/// &lt;controls:Badge Text="Low stock" Kind="Critical" Icon="Alert" /&gt;
/// </code>
/// </example>
public sealed class Badge : Control
{
    /// <summary>The label.</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(Badge), new FrameworkPropertyMetadata(string.Empty, (d, e) =>
        {
            ComponentHelpers.SetName(d, e.NewValue as string);
            ((Badge)d).UpdateTrimTip();
        }));

    /// <summary>The colour family.</summary>
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(BadgeKind), typeof(Badge), new FrameworkPropertyMetadata(BadgeKind.Neutral));

    /// <summary>Shows a 6 px dot in the text colour before the text.</summary>
    public static readonly DependencyProperty ShowDotProperty =
        DependencyProperty.Register(nameof(ShowDot), typeof(bool), typeof(Badge), new FrameworkPropertyMetadata(false));

    /// <summary>A 13 px icon before the text; <see cref="IconKind.None"/> shows none.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(IconKind), typeof(Badge), new FrameworkPropertyMetadata(IconKind.None));

    /// <summary>The label.</summary>
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>The colour family.</summary>
    public BadgeKind Kind { get => (BadgeKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>True to show the dot before the text.</summary>
    public bool ShowDot { get => (bool)GetValue(ShowDotProperty); set => SetValue(ShowDotProperty, value); }

    /// <summary>The icon before the text.</summary>
    public IconKind Icon { get => (IconKind)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    private TextBlock? _label;
    private bool _trimTip;

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _label = GetTemplateChild("Label") as TextBlock;
    }

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateTrimTip();
    }

    /// <summary>
    /// A badge given less room than its words ends them in an ellipsis; it then shows the whole text as its tooltip, and drops that tooltip when the text fits
    /// again. A tooltip the screen set on the badge is left alone.
    /// </summary>
    private void UpdateTrimTip()
    {
        var trimmed = _label is not null && TruncationTooltip.IsTrimmed(_label);
        if (trimmed && (_trimTip || ReadLocalValue(ToolTipProperty) == DependencyProperty.UnsetValue))
        {
            ToolTip = Text;
            _trimTip = true;
        }
        else if (!trimmed && _trimTip)
        {
            ClearValue(ToolTipProperty);
            _trimTip = false;
        }
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new ComponentAutomationPeer(this, AutomationControlType.Text, nameof(Badge));
}

/// <summary>
/// A blood group tag ("O−"): Blood on BloodSoft, 12 SemiBold with tabular figures, at least 34 wide and 22 tall; <see cref="IsLarge"/> makes it 32 tall,
/// 15 px and at least 46 wide.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:BloodGroupTag Text="O−" /&gt;
/// &lt;controls:BloodGroupTag Text="AB+" IsLarge="True" /&gt;
/// </code>
/// </example>
public sealed class BloodGroupTag : Control
{
    /// <summary>The group, for example "O−" (with the real minus sign).</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(BloodGroupTag), new FrameworkPropertyMetadata(string.Empty, (d, e) => AutomationProperties.SetName(d, $"Blood group {e.NewValue as string}")));

    /// <summary>The large size (32 tall, 15 px) used in detail headers.</summary>
    public static readonly DependencyProperty IsLargeProperty =
        DependencyProperty.Register(nameof(IsLarge), typeof(bool), typeof(BloodGroupTag), new FrameworkPropertyMetadata(false));

    /// <summary>The blood group text.</summary>
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>True for the large size.</summary>
    public bool IsLarge { get => (bool)GetValue(IsLargeProperty); set => SetValue(IsLargeProperty, value); }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new ComponentAutomationPeer(this, AutomationControlType.Text, nameof(BloodGroupTag));
}

/// <summary>
/// A round avatar: initials on a Selected circle (or on BloodSoft for a donor with <see cref="IsBlood"/>), or a photo clipped to the circle.
/// The font size follows the size: round(Size x 0.375), so 12 at 32.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:Avatar Initials="AM" /&gt;
/// &lt;controls:Avatar Initials="AM" Size="40" IsBlood="True" /&gt;
/// &lt;controls:Avatar Initials="AM" Source="{Binding Photo}" /&gt;
/// </code>
/// </example>
public sealed class Avatar : Control
{
    /// <summary>One or two letters.</summary>
    public static readonly DependencyProperty InitialsProperty =
        DependencyProperty.Register(nameof(Initials), typeof(string), typeof(Avatar), new FrameworkPropertyMetadata(string.Empty, (d, e) => ComponentHelpers.SetName(d, e.NewValue as string)));

    /// <summary>A photo; when set it replaces the initials.</summary>
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(Avatar), new FrameworkPropertyMetadata(null, (d, e) => ((Avatar)d).SetValue(HasPhotoPropertyKey, IsRealPhoto(e.NewValue as ImageSource))));

    /// <summary>True for a donor: BloodSoft fill and Blood text instead of Selected and Ink2.</summary>
    public static readonly DependencyProperty IsBloodProperty =
        DependencyProperty.Register(nameof(IsBlood), typeof(bool), typeof(Avatar), new FrameworkPropertyMetadata(false));

    /// <summary>The diameter in DIP (32 by default).</summary>
    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(Avatar), new FrameworkPropertyMetadata(32.0, (d, _) => ((Avatar)d).UpdateFontSize()));

    private static readonly DependencyPropertyKey HasPhotoPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(HasPhoto), typeof(bool), typeof(Avatar), new FrameworkPropertyMetadata(false));

    /// <summary>True while a photo is shown.</summary>
    public static readonly DependencyProperty HasPhotoProperty = HasPhotoPropertyKey.DependencyProperty;

    /// <summary>Creates an avatar of the default size.</summary>
    public Avatar() => UpdateFontSize();

    /// <summary>The initials.</summary>
    public string Initials { get => (string)GetValue(InitialsProperty); set => SetValue(InitialsProperty, value); }

    /// <summary>The photo, or null to show the initials.</summary>
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>True for the blood colours.</summary>
    public bool IsBlood { get => (bool)GetValue(IsBloodProperty); set => SetValue(IsBloodProperty, value); }

    /// <summary>The diameter in DIP.</summary>
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>True when <see cref="Source"/> is a real photo.</summary>
    public bool HasPhoto => (bool)GetValue(HasPhotoProperty);

    /// <summary>
    /// A person with no photo is shown by their initials, never by a blank or generic disc: no source, and the shared placeholder drawing that the
    /// 2.0 <c>AvatarConverter</c> returns for "no photo" (<see cref="Services.DefaultAvatar.Image"/>), both count as no photo.
    /// </summary>
    private static bool IsRealPhoto(ImageSource? source) => source is not null && !ReferenceEquals(source, Services.DefaultAvatar.Image);

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new ComponentAutomationPeer(this, AutomationControlType.Image, nameof(Avatar));

    private void UpdateFontSize() => FontSize = Math.Round(Size * 0.375, MidpointRounding.AwayFromZero);
}

/// <summary>
/// A floating surface (a menu, a popover, a dialog body): Layer with a 1 px Stroke and the shadow of its <see cref="Level"/>, drawn by a separate border
/// behind the content so no effect ever touches text.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:ElevatedSurface Level="Flyout" CornerRadius="{StaticResource Radius.Panel}" Padding="{StaticResource Pad.4}"&gt;...&lt;/controls:ElevatedSurface&gt;
/// </code>
/// </example>
public sealed class ElevatedSurface : ContentControl
{
    /// <summary>How high the surface floats, which sets its shadow.</summary>
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(ElevationLevel), typeof(ElevatedSurface), new FrameworkPropertyMetadata(ElevationLevel.Flyout));

    /// <summary>The corner radius of the surface and of its shadow.</summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(ElevatedSurface), new FrameworkPropertyMetadata(new CornerRadius()));

    /// <summary>The elevation level.</summary>
    public ElevationLevel Level { get => (ElevationLevel)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    /// <summary>The corner radius.</summary>
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
}

/// <summary>
/// The right-hand detail sheet: Layer with a 1 px Stroke on its left edge, an overline, header actions and a close button in a header row, and a soft shadow
/// to the left. It slides in 24 px and fades over 200 ms (decelerating) and out over 150 ms (accelerating), and just appears and disappears when Windows
/// animation effects are off. Esc and the close button run <see cref="CloseCommand"/>, or set <see cref="IsOpen"/> to false when there is none. It never
/// takes focus by itself. Collapsed while closed.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:SidePanel IsOpen="{Binding IsDetailOpen}" Overline="{Binding Donor.Code}" CloseCommand="{Binding CloseDetailCommand}"&gt;...&lt;/controls:SidePanel&gt;
/// </code>
/// </example>
public sealed class SidePanel : ContentControl
{
    /// <summary>True to show the panel.</summary>
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(SidePanel), new FrameworkPropertyMetadata(false, (d, e) => ((SidePanel)d).OnIsOpenChanged((bool)e.NewValue)));

    /// <summary>The small muted line at the left of the header row, such as "Donor D-0175".</summary>
    public static readonly DependencyProperty OverlineProperty =
        DependencyProperty.Register(nameof(Overline), typeof(string), typeof(SidePanel), new FrameworkPropertyMetadata(null, (d, e) => ComponentHelpers.SetName(d, e.NewValue as string)));

    /// <summary>Content before the close button (a "..." menu button).</summary>
    public static readonly DependencyProperty HeaderActionsProperty =
        DependencyProperty.Register(nameof(HeaderActions), typeof(object), typeof(SidePanel), new FrameworkPropertyMetadata(null));

    /// <summary>Run by the close button and Esc; when null they set <see cref="IsOpen"/> to false.</summary>
    public static readonly DependencyProperty CloseCommandProperty =
        DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(SidePanel), new FrameworkPropertyMetadata(null));

    /// <summary>The width of the panel in DIP (420, or 352 in a compact window).</summary>
    public static readonly DependencyProperty PanelWidthProperty =
        DependencyProperty.Register(nameof(PanelWidth), typeof(double), typeof(SidePanel), new FrameworkPropertyMetadata(420.0));

    /// <summary>
    /// True on a side panel and on everything inside it (inherited; the panel sets it on itself). A scrolling body styled <c>ScrollViewer.Panel</c> reads it
    /// to reach through the panel's right padding, so the scroll bar lies in the padding and the content keeps its 24 DIP right edge.
    /// </summary>
    public static readonly DependencyProperty IsInsideProperty =
        DependencyProperty.RegisterAttached("IsInside", typeof(bool), typeof(SidePanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    private Button? _closeButton;
    private FrameworkElement? _host;
    private TranslateTransform? _slide;
    private int _version;

    /// <summary>Creates a closed (collapsed) panel.</summary>
    public SidePanel()
    {
        Visibility = Visibility.Collapsed;
        Focusable = false;
        SetIsInside(this, true);
    }

    public static bool GetIsInside(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(IsInsideProperty);

    public static void SetIsInside(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(IsInsideProperty, value);

    /// <summary>True while the panel is open.</summary>
    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    /// <summary>The header line.</summary>
    public string? Overline { get => (string?)GetValue(OverlineProperty); set => SetValue(OverlineProperty, value); }

    /// <summary>Content before the close button.</summary>
    public object? HeaderActions { get => GetValue(HeaderActionsProperty); set => SetValue(HeaderActionsProperty, value); }

    /// <summary>The close command.</summary>
    public ICommand? CloseCommand { get => (ICommand?)GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    /// <summary>The panel width.</summary>
    public double PanelWidth { get => (double)GetValue(PanelWidthProperty); set => SetValue(PanelWidthProperty, value); }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_closeButton is not null)
        {
            _closeButton.Click -= OnCloseClick;
        }

        _closeButton = GetTemplateChild("PART_Close") as Button;
        if (_closeButton is not null)
        {
            _closeButton.Click += OnCloseClick;
        }

        // The slide transform is made here, so it is never a frozen template copy and can be animated.
        _host = GetTemplateChild("PART_Host") as FrameworkElement;
        _slide = null;
        if (_host is not null)
        {
            _slide = new TranslateTransform();
            _host.RenderTransform = _slide;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && IsOpen && !e.Handled)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new ComponentAutomationPeer(this, AutomationControlType.Pane, nameof(SidePanel));

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void Close() => ComponentHelpers.RunClose(CloseCommand, () => IsOpen = false);

    private void OnIsOpenChanged(bool isOpen)
    {
        var version = ++_version;
        var host = _host;
        var slide = _slide;
        var animate = host is not null && slide is not null && MotionSettings.AnimationsEnabled;
        if (isOpen)
        {
            Visibility = Visibility.Visible;
            if (animate)
            {
                var duration = MotionSettings.PanelOpen;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                slide!.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(ThemeTokens.Space("Motion.SlideDistance"), 0, duration) { EasingFunction = ease });
                host!.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            }
            else
            {
                ClearAnimations(host, slide);
            }

            return;
        }

        if (!animate)
        {
            ClearAnimations(host, slide);
            Visibility = Visibility.Collapsed;
            return;
        }

        var closeDuration = MotionSettings.PanelClose;
        var accelerate = new CubicEase { EasingMode = EasingMode.EaseIn };
        slide!.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, ThemeTokens.Space("Motion.SlideDistance"), closeDuration) { EasingFunction = accelerate });
        var fade = new DoubleAnimation(1, 0, closeDuration) { EasingFunction = accelerate };
        fade.Completed += (_, _) =>
        {
            if (version == _version && !IsOpen)
            {
                ClearAnimations(host, slide);
                Visibility = Visibility.Collapsed;
            }
        };
        host!.BeginAnimation(OpacityProperty, fade);
    }

    private static void ClearAnimations(FrameworkElement? host, TranslateTransform? slide)
    {
        host?.BeginAnimation(OpacityProperty, null);
        slide?.BeginAnimation(TranslateTransform.XProperty, null);
    }
}

/// <summary>
/// The dialog container: Layer, radius 12, a 1 px Stroke and the dialog shadow behind it; a 20/28 title, the body, and an optional footer row for the buttons
/// (right-aligned, a 1 px Stroke above it). At most 560 wide. Put it inside a <see cref="ModalOverlay"/>.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:DialogSurface Title="Delete donor?"&gt;
///   &lt;TextBlock Style="{StaticResource Text.Body}" Text="This cannot be undone." /&gt;
///   &lt;controls:DialogSurface.Footer&gt;
///     &lt;StackPanel Orientation="Horizontal"&gt;&lt;Button Style="{StaticResource Button.Secondary}" Content="Cancel" /&gt;&lt;Button Style="{StaticResource Button.Danger}" Content="Delete donor" /&gt;&lt;/StackPanel&gt;
///   &lt;/controls:DialogSurface.Footer&gt;
/// &lt;/controls:DialogSurface&gt;
/// </code>
/// </example>
public sealed class DialogSurface : ContentControl
{
    /// <summary>The dialog title (20/28 SemiBold). Also the automation name.</summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(DialogSurface), new FrameworkPropertyMetadata(string.Empty, (d, e) => ComponentHelpers.SetName(d, e.NewValue as string)));

    /// <summary>The buttons row, right-aligned; the footer is hidden when null. Spacing between buttons is up to the caller.</summary>
    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.Register(nameof(Footer), typeof(object), typeof(DialogSurface), new FrameworkPropertyMetadata(null));

    /// <summary>The title.</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>The footer content.</summary>
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new ComponentAutomationPeer(this, AutomationControlType.Pane, nameof(DialogSurface));
}

/// <summary>
/// A full-size modal layer: a scrim (a click on it runs <see cref="CloseCommand"/>), the content centred, a fade and a scale from 0.98 to 1 over 180 ms on
/// opening, Esc to close, and Tab that cycles inside the content. Focus moves into the content when it opens and returns to where it was when it closes.
/// Collapsed while closed. Place it as the last child of the window's root grid so it covers everything.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:ModalOverlay IsOpen="{Binding IsDialogOpen}" CloseCommand="{Binding CancelCommand}"&gt;
///   &lt;controls:DialogSurface Title="Record donation"&gt;...&lt;/controls:DialogSurface&gt;
/// &lt;/controls:ModalOverlay&gt;
/// </code>
/// </example>
public sealed class ModalOverlay : ContentControl
{
    /// <summary>True to show the overlay.</summary>
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(ModalOverlay), new FrameworkPropertyMetadata(false, (d, e) => ((ModalOverlay)d).OnIsOpenChanged((bool)e.NewValue)));

    /// <summary>Run by a click on the scrim and by Esc; when null they set <see cref="IsOpen"/> to false.</summary>
    public static readonly DependencyProperty CloseCommandProperty =
        DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(ModalOverlay), new FrameworkPropertyMetadata(null));

    private UIElement? _scrim;
    private FrameworkElement? _root;
    private FrameworkElement? _host;
    private ScaleTransform? _scale;
    private IInputElement? _restoreFocus;

    /// <summary>Creates a closed (collapsed) overlay.</summary>
    public ModalOverlay()
    {
        Visibility = Visibility.Collapsed;
        Focusable = false;
    }

    /// <summary>True while the overlay is open.</summary>
    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    /// <summary>The close command.</summary>
    public ICommand? CloseCommand { get => (ICommand?)GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_scrim is not null)
        {
            _scrim.MouseLeftButtonDown -= OnScrimMouseDown;
        }

        _scrim = GetTemplateChild("PART_Scrim") as UIElement;
        if (_scrim is not null)
        {
            _scrim.MouseLeftButtonDown += OnScrimMouseDown;
        }

        // The scale transform is made here, so it is never a frozen template copy and can be animated.
        _root = GetTemplateChild("PART_Root") as FrameworkElement;
        _host = GetTemplateChild("PART_Host") as FrameworkElement;
        _scale = null;
        if (_host is not null)
        {
            _scale = new ScaleTransform();
            _host.RenderTransform = _scale;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && IsOpen && !e.Handled)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new ComponentAutomationPeer(this, AutomationControlType.Pane, nameof(ModalOverlay));

    private void OnScrimMouseDown(object sender, MouseButtonEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void Close() => ComponentHelpers.RunClose(CloseCommand, () => IsOpen = false);

    private void OnIsOpenChanged(bool isOpen)
    {
        if (!isOpen)
        {
            Visibility = Visibility.Collapsed;
            if (_restoreFocus is UIElement { IsVisible: true } previous)
            {
                previous.Focus();
            }

            _restoreFocus = null;
            return;
        }

        _restoreFocus = Keyboard.FocusedElement;
        Visibility = Visibility.Visible;
        var root = _root;
        var host = _host;
        var scale = _scale;
        if (root is not null && scale is not null && MotionSettings.AnimationsEnabled)
        {
            var duration = MotionSettings.Standard;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var from = ThemeTokens.Space("Motion.DialogScaleFrom");
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, 1, duration) { EasingFunction = ease });
        }
        else
        {
            root?.BeginAnimation(OpacityProperty, null);
            scale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        if (host is not null)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (IsOpen && !host.IsKeyboardFocusWithin)
                    {
                        host.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    }
                }));
        }
    }
}

/// <summary>
/// A box that asks for no more than <see cref="Cap"/> of height for its content, but takes all the height it is given. A scrolling list beside a panel
/// of a different height uses it: the list asks for a few rows, is as tall as its neighbour, and scrolls when it has more rows than room. With the
/// default cap (no limit) it is a plain decorator and the content has its natural height.
/// <code>
/// &lt;controls:CappedHeightBox Cap="272"&gt;&lt;ScrollViewer&gt;...&lt;/ScrollViewer&gt;&lt;/controls:CappedHeightBox&gt;
/// </code>
/// </summary>
public sealed class CappedHeightBox : Decorator
{
    /// <summary>The most height the box asks for, in DIP (no limit by default).</summary>
    public static readonly DependencyProperty CapProperty =
        DependencyProperty.Register(nameof(Cap), typeof(double), typeof(CappedHeightBox), new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The most height the box asks for.</summary>
    public double Cap { get => (double)GetValue(CapProperty); set => SetValue(CapProperty, value); }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            return default;
        }

        // Measured within the cap, so a scroll viewer inside asks for the cap at most; arranged with whatever the box gets (Decorator does that).
        child.Measure(new Size(constraint.Width, Math.Min(constraint.Height, Cap)));
        return child.DesiredSize;
    }
}

/// <summary>
/// Keeps a blood group in one piece when a sentence wraps. WPF may break a line before the minus sign (U+2212), which leaves "(O" at the end of one
/// line and "−)" at the start of the next; a word joiner (U+2060, no width) before each minus sign forbids that break. Use it on wrapping text that
/// names blood groups: <c>Text="{Binding Detail, Converter={StaticResource KeepGroups}}"</c>. The bound text itself is unchanged (tooltips, automation
/// names and copies keep the plain text).
/// </summary>
public sealed class KeepGroupsTogetherConverter : System.Windows.Data.IValueConverter
{
    private const string Minus = "\u2212";
    private const string JoinedMinus = "\u2060\u2212";

    /// <summary>The text with a word joiner before every minus sign.</summary>
    public static string Join(string? text) => string.IsNullOrEmpty(text) ? string.Empty : text.Replace(JoinedMinus, Minus, StringComparison.Ordinal).Replace(Minus, JoinedMinus, StringComparison.Ordinal);

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Join(value as string);

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
