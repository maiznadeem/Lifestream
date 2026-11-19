using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lifestream.Client.Shell;
using Lifestream.Desktop.Chrome;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Shell;

/// <summary>
/// The main window's content (direction A): the title bar (mark, search field that opens the palette, notifications, account), the sidebar (full,
/// or an icon rail when collapsed with Ctrl+B or when the window is compact), the page on the canvas, toasts and the command palette. It adapts to
/// the window's breakpoint (Controls/Responsive): below 1280 DIP the sidebar is a rail, the title bar is 40 DIP and the search collapses to a button.
/// F6 moves focus between the regions: title bar, sidebar, page.
/// </summary>
public partial class ShellView : UserControl
{
    /// <summary>The space kept free at the right of the title bar for the window's caption buttons.</summary>
    public static readonly GridLength CaptionButtonsColumn = new(ChromeWindow.CaptionButtonsWidth);

    public static readonly DependencyProperty IsRailProperty =
        DependencyProperty.Register(nameof(IsRail), typeof(bool), typeof(ShellView), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty HasAttentionProperty =
        DependencyProperty.Register(nameof(HasAttention), typeof(bool), typeof(ShellView), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty NotificationsNameProperty =
        DependencyProperty.Register(nameof(NotificationsName), typeof(string), typeof(ShellView), new FrameworkPropertyMetadata("Notifications"));

    private static readonly DependencyPropertyDescriptor BreakpointDescriptor = DependencyPropertyDescriptor.FromProperty(Responsive.BreakpointProperty, typeof(Grid));

    private ShellViewModel? _model;

    public ShellView()
    {
        InitializeComponent();
        BreakpointDescriptor.AddValueChanged(Root, OnBreakpointChanged);
        DataContextChanged += OnDataContextChanged;
        PageHost.LayoutUpdated += (_, _) => UpdateLegacyTitle();
        PreviewKeyDown += OnPreviewKeyDown;
        Unloaded += (_, _) => BreakpointDescriptor.RemoveValueChanged(Root, OnBreakpointChanged);
        Apply();
    }

    /// <summary>True while the sidebar shows as the 52 DIP icon rail.</summary>
    public bool IsRail { get => (bool)GetValue(IsRailProperty); private set => SetValue(IsRailProperty, value); }

    /// <summary>True when something needs attention (blood groups below their minimum): the bell shows a red dot.</summary>
    public bool HasAttention { get => (bool)GetValue(HasAttentionProperty); private set => SetValue(HasAttentionProperty, value); }

    /// <summary>The bell's name and tooltip: what needs attention, and that it opens the dashboard.</summary>
    public string NotificationsName { get => (string)GetValue(NotificationsNameProperty); private set => SetValue(NotificationsNameProperty, value); }

    /// <summary>The current breakpoint of the shell (for tests).</summary>
    public Breakpoint Breakpoint => Responsive.GetBreakpoint(Root);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = e.NewValue as ShellViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
        }

        Apply();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsNavCollapsed) or nameof(ShellViewModel.LowStockGroups))
        {
            Apply();
        }
    }

    private void OnBreakpointChanged(object? sender, EventArgs e) => Apply();

    /// <summary>Lays the shell out for the breakpoint and the person's sidebar choice.</summary>
    private void Apply()
    {
        var compact = Responsive.GetBreakpoint(Root) == Breakpoint.Compact;
        IsRail = compact || _model?.IsNavCollapsed == true;
        NavColumn.Width = new GridLength(ThemeTokens.Space(IsRail ? "Width.Rail" : "Width.Sidebar"));
        Nav.Margin = IsRail ? new Thickness(0, 6, 0, 12) : new Thickness(8, 6, 8, 12);
        AccountText.Visibility = IsRail ? Visibility.Collapsed : Visibility.Visible;
        AccountButton.HorizontalAlignment = IsRail ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        AccountButton.Padding = IsRail ? new Thickness(4) : new Thickness(12, 10, 12, 10);
        Wordmark.Visibility = Visibility.Visible;

        var titleHeight = ThemeTokens.Space(compact ? "Height.TitleBarCompact" : "Height.TitleBar");
        TitleRow.Height = new GridLength(titleHeight);
        if (Window.GetWindow(this) is ChromeWindow window)
        {
            window.TitleBarHeight = titleHeight;
        }

        SearchField.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactSearch.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        NotificationsButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AvatarButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PageHost.Margin = compact ? new Thickness(22, 18, 16, 16) : new Thickness(32, 20, 32, 24);
        LegacyTitle.Margin = compact ? new Thickness(22, 18, 16, 0) : new Thickness(32, 24, 32, 0);

        var low = _model?.LowStockGroups ?? 0;
        HasAttention = low > 0;
        NotificationsName = low switch
        {
            0 => "Notifications: nothing needs attention",
            1 => "Notifications: 1 blood group is below its minimum. Opens the dashboard.",
            _ => $"Notifications: {low} blood groups are below their minimum. Opens the dashboard.",
        };
    }

    /// <summary>A page that draws its own page header (the direction A PageHeader) hides the shell's plain title.</summary>
    private void UpdateLegacyTitle()
    {
        var owns = FindPage() is { } page && ShellPage.GetOwnsHeader(page);
        var visibility = owns ? Visibility.Collapsed : Visibility.Visible;
        if (LegacyTitle.Visibility != visibility)
        {
            LegacyTitle.Visibility = visibility;
            PageHost.Margin = new Thickness(PageHost.Margin.Left, owns ? (Breakpoint == Breakpoint.Compact ? 18 : 24) : 20, PageHost.Margin.Right, PageHost.Margin.Bottom);
        }
    }

    private FrameworkElement? FindPage() =>
        VisualTreeHelper.GetChildrenCount(PageHost) > 0 && VisualTreeHelper.GetChild(PageHost, 0) is ContentPresenter presenter && VisualTreeHelper.GetChildrenCount(presenter) > 0
            ? VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement
            : null;

    /// <summary>The account menu opens on click and on Enter or Space (the buttons are focusable), so it is reachable by keyboard.</summary>
    private void OnAccountClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && Resources["AccountMenu"] is ContextMenu menu)
        {
            menu.DataContext = DataContext;
            menu.PlacementTarget = button;
            menu.Placement = button == AvatarButton ? System.Windows.Controls.Primitives.PlacementMode.Bottom : System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        }
    }

    /// <summary>The bell opens the dashboard, whose "Needs attention" list holds what the dot announces.</summary>
    private void OnNotificationsClick(object sender, RoutedEventArgs e) => _model?.NavigateToIndexCommand.Execute(1);

    /// <summary>F6 and Shift+F6 cycle the regions: title bar, sidebar, page (and the palette's own field while it is open).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F6 || Palette.IsOpen)
        {
            return;
        }

        var regions = new FrameworkElement[] { SearchField.IsVisible ? SearchField : CompactSearch, Nav, PageHost };
        var current = Array.FindIndex(regions, r => r.IsKeyboardFocusWithin);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
        for (var i = 1; i <= regions.Length; i++)
        {
            var next = regions[((current < 0 ? (step > 0 ? -1 : 0) : current) + (step * i) + (regions.Length * 2)) % regions.Length];
            if (FocusRegion(next))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private bool FocusRegion(FrameworkElement region)
    {
        if (region.Focusable && region.IsVisible)
        {
            return region.Focus();
        }

        if (region == Nav)
        {
            var selected = FindVisual<RadioButton>(Nav, r => r.IsChecked == true) ?? FindVisual<RadioButton>(Nav, _ => true);
            return selected?.Focus() == true;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => region.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
        return region.IsVisible;
    }

    private static T? FindVisual<T>(DependencyObject root, Func<T, bool> match)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && match(found))
            {
                return found;
            }

            if (FindVisual(child, match) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
