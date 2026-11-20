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
/// The main window's content (direction A): the title bar (mark, search field that opens the palette, the bell with its notifications flyout, account),
/// the sidebar (full, or an icon rail when collapsed with Ctrl+B or when the window is compact), the page on the canvas, toasts and the command palette. It adapts to
/// the window's breakpoint (Controls/Responsive): below 1280 DIP the sidebar is a rail, the title bar is 40 DIP and the search collapses to a button.
/// F6 moves focus between the regions: title bar, sidebar, page.
/// </summary>
public partial class ShellView : UserControl
{
    /// <summary>The space kept free at the right of the title bar for the window's caption buttons.</summary>
    public static readonly GridLength CaptionButtonsColumn = new(ChromeWindow.CaptionButtonsWidth);

    public static readonly DependencyProperty IsRailProperty =
        DependencyProperty.Register(nameof(IsRail), typeof(bool), typeof(ShellView), new FrameworkPropertyMetadata(false));

    private static readonly DependencyPropertyDescriptor BreakpointDescriptor = DependencyPropertyDescriptor.FromProperty(Responsive.BreakpointProperty, typeof(Grid));

    private ShellViewModel? _model;

    public ShellView()
    {
        InitializeComponent();
        Notifications.Anchor = NotificationsButton;
        BreakpointDescriptor.AddValueChanged(Root, OnBreakpointChanged);
        DataContextChanged += OnDataContextChanged;
        PageHost.LayoutUpdated += (_, _) => UpdateLegacyTitle();
        PreviewKeyDown += OnPreviewKeyDown;
        Nav.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(OnNavItemChecked));
        Nav.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnNavItemClicked));
        Unloaded += (_, _) => BreakpointDescriptor.RemoveValueChanged(Root, OnBreakpointChanged);
        Apply();
    }

    /// <summary>True while the sidebar shows as the 52 DIP icon rail.</summary>
    public bool IsRail { get => (bool)GetValue(IsRailProperty); private set => SetValue(IsRailProperty, value); }

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
        if (e.PropertyName is nameof(ShellViewModel.IsNavCollapsed))
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
        Nav.Margin = IsRail ? ThemeTokens.Inset("Inset.T6B12") : ThemeTokens.Inset("Inset.L8T6R8B12");
        AccountText.Visibility = IsRail ? Visibility.Collapsed : Visibility.Visible;
        AccountButton.HorizontalAlignment = IsRail ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        AccountButton.Padding = IsRail ? ThemeTokens.Inset("Pad.4") : ThemeTokens.Inset("Inset.H12V10");
        // Beside the full sidebar and in a compact window the name follows the mark. Over a rail in a wider window (Ctrl+B) the search field starts where the
        // name would be, so the mark stands alone.
        Wordmark.Visibility = IsRail && !compact ? Visibility.Collapsed : Visibility.Visible;

        var titleHeight = ThemeTokens.Space(compact ? "Height.TitleBarCompact" : "Height.TitleBar");
        TitleRow.Height = new GridLength(titleHeight);
        if (Window.GetWindow(this) is ChromeWindow window)
        {
            window.TitleBarHeight = titleHeight;
        }

        SearchField.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactSearch.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        // The bell stays at every width: it is the only way to what needs attention from a screen other than the dashboard.
        AvatarButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ApplyPageMargin();
        LegacyTitle.Margin = compact ? ThemeTokens.Inset("Inset.L22T18R16") : ThemeTokens.Inset("Inset.L32T24R32");
    }

    /// <summary>A page that draws its own page header (the direction A PageHeader) hides the shell's plain title.</summary>
    private void UpdateLegacyTitle()
    {
        var owns = FindPage() is { } page && ShellPage.GetOwnsHeader(page);
        var visibility = owns ? Visibility.Collapsed : Visibility.Visible;
        if (LegacyTitle.Visibility != visibility)
        {
            LegacyTitle.Visibility = visibility;
            ApplyPageMargin();
        }
    }

    /// <summary>The page's inset: 32 at the sides (22/16 compact); 24 from the top for a page with its own header, 20 under the shell's title.</summary>
    private void ApplyPageMargin() =>
        PageHost.Margin = ThemeTokens.Inset(Breakpoint == Breakpoint.Compact ? "Inset.L22T18R16B16" : LegacyTitle.Visibility == Visibility.Collapsed ? "Inset.L32T24R32B24" : "Inset.L32T20R32B24");

    private FrameworkElement? FindPage() =>
        VisualTreeHelper.GetChildrenCount(PageHost) > 0 && VisualTreeHelper.GetChild(PageHost, 0) is ContentPresenter presenter && VisualTreeHelper.GetChildrenCount(presenter) > 0
            ? VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement
            : null;

    /// <summary>The account menu opens on click and on Enter or Space (the buttons are focusable), so it is reachable by keyboard.</summary>
    private void OnAccountClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && Resources["AccountMenu"] is ContextMenu menu)
        {
            menu.PlacementTarget = button;
            menu.Placement = button == AvatarButton ? System.Windows.Controls.Primitives.PlacementMode.Bottom : System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        }
    }

    private NavItem? _clickedItem;

    /// <summary>A click (pointer, Space or Enter) runs the item's command itself; remembered so <see cref="OnNavItemChecked"/> does not navigate a second time.</summary>
    private void OnNavItemClicked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is RadioButton { DataContext: NavItem item })
        {
            _clickedItem = item;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _clickedItem = null);
        }
    }

    /// <summary>
    /// A sidebar item selected without a click navigates too. UI Automation's Select (a screen reader's or a voice-control tool's way to choose a radio
    /// button) only checks the button and raises no click, so without this the item looked chosen while the page stayed where it was.
    /// </summary>
    private void OnNavItemChecked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not RadioButton { DataContext: NavItem item } button || item.IsSelected)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!ReferenceEquals(_clickedItem, item) && button.IsChecked == true && !item.IsSelected && _model?.NavigateCommand.CanExecute(item) == true)
            {
                _model.NavigateCommand.Execute(item);
            }
        });
    }

    /// <summary>F6 and Shift+F6 cycle the regions: title bar, sidebar, page (and the palette's own field while it is open).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Notifications.IsOpen)
        {
            // Esc closes the notifications wherever the focus is (in the flyout, or still on the bell).
            _model?.Notifications.Close();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.F6 || Palette.IsOpen || Notifications.IsOpen)
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
