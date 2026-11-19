using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// The auto-hide behaviour of the overlay scroll bars (Theme/ScrollBars.xaml). The implicit ScrollViewer style sets
/// <see cref="IsEnabledProperty"/>; the behaviour then fades the bars out 1.2 s after the last scroll or pointer movement over the viewer,
/// shows them again at once when the content scrolls, the pointer moves inside the viewer or enters a bar's strip, and keeps them
/// (and the widened thumb, <see cref="IsExpandedProperty"/>) while the pointer is on a strip or a thumb is being dragged.
/// With Windows "Animation effects" off nothing fades or widens: the bars are simply shown at rest width whenever the content scrolls.
/// Bars of a viewer whose content fits are collapsed by the template, so this never shows them.
/// </summary>
public static class OverlayScroll
{
    /// <summary>On a ScrollViewer: turns the auto-hide behaviour on.</summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(OverlayScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>On a ScrollBar (set by the behaviour): the pointer is on the bar's strip or its thumb is being dragged, so the track shows and the thumb is wide.</summary>
    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.RegisterAttached("IsExpanded", typeof(bool), typeof(OverlayScroll), new PropertyMetadata(false));

    private static readonly DependencyProperty ControllerProperty =
        DependencyProperty.RegisterAttached("Controller", typeof(Controller), typeof(OverlayScroll), new PropertyMetadata(null));

    /// <summary>Gets whether the auto-hide behaviour is on for a ScrollViewer.</summary>
    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    /// <summary>Turns the auto-hide behaviour on or off for a ScrollViewer.</summary>
    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    /// <summary>Gets whether a ScrollBar is expanded (track shown, thumb wide).</summary>
    public static bool GetIsExpanded(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsExpandedProperty);
    }

    /// <summary>Sets whether a ScrollBar is expanded; the behaviour does this, templates only read it.</summary>
    public static void SetIsExpanded(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsExpandedProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
        {
            return;
        }

        var existing = viewer.GetValue(ControllerProperty) as Controller;
        if ((bool)e.NewValue)
        {
            if (existing is null)
            {
                var controller = new Controller(viewer);
                viewer.SetValue(ControllerProperty, controller);
                controller.Start();
            }
        }
        else if (existing is not null)
        {
            existing.Stop();
            viewer.ClearValue(ControllerProperty);
        }
    }

    /// <summary>One per ScrollViewer: the hooks, the idle timer and the shown / hidden state of its two bars.</summary>
    private sealed class Controller
    {
        private static readonly Duration FadeOut = new(TimeSpan.FromMilliseconds(300));

        private readonly ScrollViewer _viewer;
        private readonly ScrollChangedEventHandler _scrolled;
        private readonly MouseEventHandler _pointerMoved;
        private readonly MouseEventHandler _barEntered;
        private readonly MouseEventHandler _barLeft;
        private readonly DragStartedEventHandler _dragStarted;
        private readonly DragCompletedEventHandler _dragCompleted;
        private DispatcherTimer? _idle;
        private ScrollBar? _vertical;
        private ScrollBar? _horizontal;
        private ScrollBar? _hoverBar;
        private ScrollBar? _dragBar;
        private long _lastActivity;
        private bool _attached;
        private bool _shown;

        public Controller(ScrollViewer viewer)
        {
            _viewer = viewer;
            _scrolled = OnScrollChanged;
            _pointerMoved = OnPointerMoved;
            _barEntered = OnBarEntered;
            _barLeft = OnBarLeft;
            _dragStarted = OnDragStarted;
            _dragCompleted = OnDragCompleted;
        }

        /// <summary>The behaviour only runs while the viewer is in a loaded visual tree; the timer is stopped and every hook removed on Unloaded.</summary>
        public void Start()
        {
            _viewer.Loaded += OnLoaded;
            _viewer.Unloaded += OnUnloaded;
            if (_viewer.IsLoaded)
            {
                Attach();
            }
        }

        public void Stop()
        {
            _viewer.Loaded -= OnLoaded;
            _viewer.Unloaded -= OnUnloaded;
            Detach();
        }

        private bool IsScrollable =>
            _viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible || _viewer.ComputedHorizontalScrollBarVisibility == Visibility.Visible;

        private static void SetOpacity(ScrollBar? bar, double value)
        {
            if (bar is null)
            {
                return;
            }

            bar.BeginAnimation(UIElement.OpacityProperty, null);
            bar.Opacity = value;
        }

        private static void Fade(ScrollBar? bar) => bar?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, FadeOut));

        private void OnLoaded(object sender, RoutedEventArgs e) => Attach();

        private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

        private void Attach()
        {
            if (_attached)
            {
                return;
            }

            _attached = true;
            _viewer.ScrollChanged += _scrolled;
            _viewer.PreviewMouseMove += _pointerMoved;
            _viewer.AddHandler(Thumb.DragStartedEvent, _dragStarted, true);
            _viewer.AddHandler(Thumb.DragCompletedEvent, _dragCompleted, true);
            _viewer.ApplyTemplate();
            EnsureBars();

            // Animations on: bars wait out of sight until something scrolls. Animations off: they are simply there.
            _shown = !MotionSettings.AnimationsEnabled;
            SetOpacity(_vertical, _shown ? 1 : 0);
            SetOpacity(_horizontal, _shown ? 1 : 0);
        }

        private void Detach()
        {
            if (!_attached)
            {
                return;
            }

            _attached = false;
            _idle?.Stop();
            _viewer.ScrollChanged -= _scrolled;
            _viewer.PreviewMouseMove -= _pointerMoved;
            _viewer.RemoveHandler(Thumb.DragStartedEvent, _dragStarted);
            _viewer.RemoveHandler(Thumb.DragCompletedEvent, _dragCompleted);
            Rebind(ref _vertical, null);
            Rebind(ref _horizontal, null);
            _hoverBar = null;
            _dragBar = null;
        }

        private void EnsureBars()
        {
            if (_viewer.Template is not { } template)
            {
                return;
            }

            Rebind(ref _vertical, template.FindName("PART_VerticalScrollBar", _viewer) as ScrollBar);
            Rebind(ref _horizontal, template.FindName("PART_HorizontalScrollBar", _viewer) as ScrollBar);
        }

        private void Rebind(ref ScrollBar? slot, ScrollBar? found)
        {
            if (ReferenceEquals(slot, found))
            {
                return;
            }

            if (slot is not null)
            {
                slot.MouseEnter -= _barEntered;
                slot.MouseLeave -= _barLeft;
                SetExpanded(slot, false);
            }

            slot = found;
            if (found is not null)
            {
                found.MouseEnter += _barEntered;
                found.MouseLeave += _barLeft;
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, _viewer) && (e.VerticalChange != 0 || e.HorizontalChange != 0))
            {
                Reveal();
            }
        }

        private void OnPointerMoved(object sender, MouseEventArgs e)
        {
            if (IsScrollable)
            {
                Reveal();
            }
        }

        private void OnBarEntered(object sender, MouseEventArgs e)
        {
            _hoverBar = sender as ScrollBar;
            UpdateExpanded();
            Reveal();
        }

        private void OnBarLeft(object sender, MouseEventArgs e)
        {
            if (ReferenceEquals(_hoverBar, sender))
            {
                _hoverBar = null;
            }

            UpdateExpanded();
            Reveal();
        }

        private void OnDragStarted(object sender, DragStartedEventArgs e)
        {
            _dragBar = OwnBar(e.OriginalSource);
            UpdateExpanded();
            Reveal();
        }

        private void OnDragCompleted(object sender, DragCompletedEventArgs e)
        {
            _dragBar = null;
            UpdateExpanded();
            Reveal();
        }

        /// <summary>The viewer's own bar that a thumb belongs to (data grid column grips and nested viewers' bars are not ours).</summary>
        private ScrollBar? OwnBar(object? source)
        {
            for (var node = source as Visual; node is not null; node = VisualTreeHelper.GetParent(node) as Visual)
            {
                if (node is ScrollBar bar)
                {
                    return ReferenceEquals(bar, _vertical) || ReferenceEquals(bar, _horizontal) ? bar : null;
                }
            }

            return null;
        }

        private void UpdateExpanded()
        {
            SetExpanded(_vertical, IsHeld(_vertical));
            SetExpanded(_horizontal, IsHeld(_horizontal));
        }

        private bool IsHeld(ScrollBar? bar) =>
            bar is not null && MotionSettings.AnimationsEnabled && (ReferenceEquals(_hoverBar, bar) || ReferenceEquals(_dragBar, bar));

        private static void SetExpanded(ScrollBar? bar, bool value)
        {
            if (bar is not null)
            {
                SetIsExpanded(bar, value);
            }
        }

        /// <summary>Shows the bars at once and (re)starts the countdown to fading them, unless the pointer or a drag holds them.</summary>
        private void Reveal()
        {
            if (_vertical is null && _horizontal is null)
            {
                EnsureBars();
            }

            if (!_shown)
            {
                _shown = true;
                SetOpacity(_vertical, 1);
                SetOpacity(_horizontal, 1);
            }

            if (!MotionSettings.AnimationsEnabled)
            {
                return;
            }

            _lastActivity = Stopwatch.GetTimestamp();
            if (_hoverBar is null && _dragBar is null && _idle is not { IsEnabled: true })
            {
                Arm(MotionSettings.ScrollIdle);
            }
        }

        private void Arm(TimeSpan delay)
        {
            if (_idle is null)
            {
                _idle = new DispatcherTimer(DispatcherPriority.Background, _viewer.Dispatcher);
                _idle.Tick += OnIdleTick;
            }

            _idle.Stop();
            _idle.Interval = delay;
            _idle.Start();
        }

        private void OnIdleTick(object? sender, EventArgs e)
        {
            _idle?.Stop();
            if (!_shown || _hoverBar is not null || _dragBar is not null || !MotionSettings.AnimationsEnabled)
            {
                return;
            }

            // Activity since this timer was armed only moves the deadline; the timer is not restarted on every mouse move.
            var remaining = MotionSettings.ScrollIdle - Stopwatch.GetElapsedTime(_lastActivity);
            if (remaining > TimeSpan.Zero)
            {
                Arm(remaining);
                return;
            }

            _shown = false;
            Fade(_vertical);
            Fade(_horizontal);
        }
    }
}
