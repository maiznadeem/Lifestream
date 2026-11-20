using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lifestream.Client.Common;
using Lifestream.Client.Shell;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Shell;

/// <summary>
/// The notifications flyout of the bell (the view of <see cref="NotificationsViewModel"/>). It opens under its <see cref="Anchor"/> (the bell), right
/// edges aligned, and never taller than the room under it (the list scrolls). Opening it moves the keyboard focus to its first action, Tab stays
/// inside, and Esc, a click outside it or the bell again close it and give the focus back to the bell.
/// </summary>
public partial class NotificationsFlyout : UserControl
{
    /// <summary>The gap between the bell and the flyout, and between the flyout and the window's edges.</summary>
    private const double Gap = 6;

    private const double EdgeGap = 12;

    private NotificationsViewModel? _model;

    public NotificationsFlyout()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => Place();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>The control the flyout hangs under (the bell).</summary>
    public FrameworkElement? Anchor { get; set; }

    /// <summary>True while the flyout is shown.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = e.NewValue as NotificationsViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
        }

        ApplyState();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NotificationsViewModel.IsOpen):
                if (_model!.IsOpen)
                {
                    Show();
                }
                else
                {
                    Hide();
                }

                break;
            case nameof(NotificationsViewModel.State) or nameof(NotificationsViewModel.Count):
                ApplyState();
                break;
        }
    }

    /// <summary>One of four bodies shows: the items; "nothing needs attention"; the error with Retry; or, while there is nothing to show yet, "checking".</summary>
    private void ApplyState()
    {
        var state = _model?.State ?? LoadState.Loading;
        var hasItems = _model is { Count: > 0 };
        var failed = state == LoadState.Error;
        ErrorState.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        ItemsList.Visibility = hasItems && !failed ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = !hasItems && state == LoadState.Empty ? Visibility.Visible : Visibility.Collapsed;
        LoadingState.Visibility = !hasItems && state is LoadState.Loading or LoadState.Waking ? Visibility.Visible : Visibility.Collapsed;
        CountText.Visibility = hasItems && !failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Show()
    {
        ApplyState();
        Visibility = Visibility.Visible;
        UpdateLayout();
        Place();
        Surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, MotionSettings.Fast));

        // Focus goes into the flyout, so the keyboard and a screen reader are on its first action at once (the items' buttons, or "View all").
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsOpen && !Surface.IsKeyboardFocusWithin)
            {
                Surface.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        });
    }

    private void Hide()
    {
        var hadFocus = IsKeyboardFocusWithin;
        Visibility = Visibility.Collapsed;
        if (hadFocus && Anchor is { IsVisible: true } anchor)
        {
            anchor.Focus();
        }
    }

    /// <summary>Puts the surface under the anchor, right edges aligned, inside the window, and caps its height to the room below.</summary>
    private void Place()
    {
        if (!IsOpen || Anchor is not { IsVisible: true } anchor || PresentationSource.FromVisual(anchor) is null || PresentationSource.FromVisual(this) is null)
        {
            return;
        }

        var below = anchor.TranslatePoint(new Point(anchor.ActualWidth, anchor.ActualHeight), this);
        var top = Math.Max(Gap, below.Y + Gap);
        var left = Math.Min(Math.Max(EdgeGap, below.X - Surface.Width), Math.Max(EdgeGap, ActualWidth - Surface.Width - EdgeGap));
        Canvas.SetLeft(Surface, Math.Round(left));
        Canvas.SetTop(Surface, Math.Round(top));
        Surface.MaxHeight = Math.Max(160, ActualHeight - top - EdgeGap);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsOpen)
        {
            _model?.Close();
            e.Handled = true;
        }
    }

    private void OnLayerMouseDown(object sender, MouseButtonEventArgs e) => _model?.Close();

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
