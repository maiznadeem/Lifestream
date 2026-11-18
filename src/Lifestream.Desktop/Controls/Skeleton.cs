using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Controls;

/// <summary>The surfaces that have a skeleton, each shaped like the real content (SHELL-28).</summary>
public enum SkeletonKind
{
    List,
    Detail,
    Dashboard,
    StockTiles,
    Picker,
    Profile,
}

/// <summary>
/// A placeholder in the shape of the surface it stands in for: <c>&lt;controls:Skeleton Kind="List" ColumnWidths="2*,*,90"/&gt;</c>.
/// It shimmers (1.2 s) unless Windows animation effects are off, in which case it is static. It never takes focus and is
/// hidden from UI Automation except for one polite "Loading" name.
/// </summary>
public sealed class Skeleton : Decorator
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(SkeletonKind), typeof(Skeleton), new PropertyMetadata(SkeletonKind.List, (d, _) => ((Skeleton)d).Rebuild()));

    /// <summary>Column widths of the grid this skeleton stands in for, in XAML GridLength syntax: "2*,*,120,90".</summary>
    public static readonly DependencyProperty ColumnWidthsProperty =
        DependencyProperty.Register(nameof(ColumnWidths), typeof(string), typeof(Skeleton), new PropertyMetadata("2*,*,*,*", (d, _) => ((Skeleton)d).Rebuild()));

    public static readonly DependencyProperty RowCountProperty =
        DependencyProperty.Register(nameof(RowCount), typeof(int), typeof(Skeleton), new PropertyMetadata(10, (d, _) => ((Skeleton)d).Rebuild()));

    /// <summary>Every bar is this many DIP tall inside a row; also the tile and avatar sizes derive from the row height token.</summary>
    public const double BarHeight = 12;

    private LinearGradientBrush? _shimmer;
    private TranslateTransform? _slide;
    private Brush? _barBrush;

    public Skeleton()
    {
        Loaded += (_, _) => Rebuild();
        Unloaded += (_, _) => StopShimmer();
        Focusable = false;
        KeyboardNavigation.SetIsTabStop(this, false);
        System.Windows.Automation.AutomationProperties.SetName(this, "Loading");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(this, System.Windows.Automation.AutomationLiveSetting.Polite);
    }

    public SkeletonKind Kind
    {
        get => (SkeletonKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string ColumnWidths
    {
        get => (string)GetValue(ColumnWidthsProperty);
        set => SetValue(ColumnWidthsProperty, value);
    }

    public int RowCount
    {
        get => (int)GetValue(RowCountProperty);
        set => SetValue(RowCountProperty, value);
    }

    /// <summary>True while the shimmer storyboard is running; false when animations are off (static skeleton).</summary>
    public bool IsAnimating { get; private set; }

    /// <summary>The bars of this skeleton in layout order (for tests and for matching a grid).</summary>
    public IEnumerable<FrameworkElement> Bars => Descendants(Child).Where(e => e is Border { Tag: "bar" }).Cast<FrameworkElement>();

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject? root)
    {
        if (root is null)
        {
            yield break;
        }

        foreach (var index in Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(root)))
        {
            if (VisualTreeHelper.GetChild(root, index) is FrameworkElement child)
            {
                yield return child;
                foreach (var inner in Descendants(child))
                {
                    yield return inner;
                }
            }
        }
    }

    private void Rebuild()
    {
        if (!IsLoaded)
        {
            return;
        }

        StopShimmer();
        if (TryFindResource("Color.SkeletonBase") is not Color baseColor || TryFindResource("Color.SkeletonHighlight") is not Color highlight)
        {
            Child = null;
            return;
        }

        _barBrush = MotionSettings.AnimationsEnabled ? CreateShimmer(baseColor, highlight) : new SolidColorBrush(baseColor);
        Child = Kind switch
        {
            SkeletonKind.List => BuildList(),
            SkeletonKind.Detail => BuildDetail(),
            SkeletonKind.Dashboard => BuildDashboard(),
            SkeletonKind.StockTiles => BuildTiles(),
            SkeletonKind.Picker => BuildPicker(),
            SkeletonKind.Profile => BuildProfile(),
            _ => null,
        };
        if (MotionSettings.AnimationsEnabled && _slide is not null)
        {
            StartShimmer();
        }
    }

    private LinearGradientBrush CreateShimmer(Color baseColor, Color highlight)
    {
        _slide = new TranslateTransform(-1, 0);
        _shimmer = new LinearGradientBrush(
            [new GradientStop(baseColor, 0), new GradientStop(highlight, 0.5), new GradientStop(baseColor, 1)],
            new Point(0, 0),
            new Point(1, 0))
        {
            RelativeTransform = _slide,
        };
        return _shimmer;
    }

    private void StartShimmer()
    {
        var slide = new DoubleAnimation(-1, 1, MotionSettings.Shimmer) { RepeatBehavior = RepeatBehavior.Forever };
        _slide!.BeginAnimation(TranslateTransform.XProperty, slide);
        IsAnimating = true;
    }

    private void StopShimmer()
    {
        _slide?.BeginAnimation(TranslateTransform.XProperty, null);
        IsAnimating = false;
    }

    private Border Bar(double width = double.NaN, double height = BarHeight, double radius = 4, HorizontalAlignment align = HorizontalAlignment.Stretch) => new()
    {
        Tag = "bar",
        Width = width,
        Height = height,
        CornerRadius = new CornerRadius(radius),
        Background = _barBrush,
        HorizontalAlignment = align,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private double Token(string key, double fallback) => TryFindResource(key) is double d ? d : fallback;

    private StackPanel BuildList()
    {
        var rowHeight = Token("Height.Row", 40);
        var root = new StackPanel { Orientation = Orientation.Vertical };

        // Toolbar: a search box and two buttons, the same height as the real toolbar controls.
        var toolbar = new Grid { Height = Token("Height.Control", 40), Margin = new Thickness(0, 0, 0, Token("Space.16", 16)) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(Bar(height: Token("Height.Control", 40), radius: 4));
        var action = Bar(width: 128, height: Token("Height.Control", 40), radius: 4);
        Grid.SetColumn(action, 2);
        toolbar.Children.Add(action);
        root.Children.Add(toolbar);

        var converter = new GridLengthConverter();
        var widths = ColumnWidths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => (GridLength)converter.ConvertFromString(null, CultureInfo.InvariantCulture, w)!)
            .ToList();
        for (var row = 0; row < RowCount; row++)
        {
            var line = new Grid { Height = rowHeight };
            foreach (var width in widths)
            {
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            }

            for (var column = 0; column < widths.Count; column++)
            {
                // The bar starts where the grid's cell text starts (cell padding 12) and is a little shorter than the cell.
                var bar = Bar();
                bar.Margin = new Thickness(12, 0, 12, 0);
                Grid.SetColumn(bar, column);
                line.Children.Add(bar);
            }

            var separator = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = TryFindResource("Brush.BorderSubtle") as Brush };
            Grid.SetColumnSpan(separator, Math.Max(widths.Count, 1));
            line.Children.Add(separator);
            root.Children.Add(line);
        }

        return root;
    }

    private StackPanel BuildDetail()
    {
        var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 0) };
        root.Children.Add(new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(36), Background = _barBrush, Tag = "bar", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 24) });
        for (var i = 0; i < 6; i++)
        {
            root.Children.Add(LabelValueRow());
        }

        return root;
    }

    private StackPanel BuildProfile()
    {
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(48), Background = _barBrush, Tag = "bar", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 24) });
        for (var i = 0; i < 9; i++)
        {
            root.Children.Add(LabelValueRow());
        }

        return root;
    }

    private Grid LabelValueRow()
    {
        var row = new Grid { Height = 56 };
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Bar(width: 96, height: 10, align: HorizontalAlignment.Left));
        var value = Bar(height: 16, radius: 4);
        value.MaxWidth = 360;
        value.HorizontalAlignment = HorizontalAlignment.Left;
        value.Width = 280;
        Grid.SetRow(value, 1);
        row.Children.Add(value);
        return row;
    }

    private Grid BuildDashboard()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(280) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var kpis = new UniformGrid { Rows = 1, Columns = 4 };
        for (var i = 0; i < 4; i++)
        {
            kpis.Children.Add(Block(height: 80, margin: new Thickness(i == 0 ? 0 : 8, 0, i == 3 ? 0 : 8, 0), radius: 8));
        }

        root.Children.Add(kpis);
        var chart = Block(height: 264, margin: new Thickness(0, 16, 0, 0), radius: 8);
        Grid.SetRow(chart, 1);
        root.Children.Add(chart);

        var table = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        for (var i = 0; i < 5; i++)
        {
            var row = new Grid { Height = Token("Height.Row", 40) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var c = 0; c < 3; c++)
            {
                var bar = Bar();
                bar.Margin = new Thickness(12, 0, 12, 0);
                Grid.SetColumn(bar, c);
                row.Children.Add(bar);
            }

            table.Children.Add(row);
        }

        Grid.SetRow(table, 2);
        root.Children.Add(table);
        return root;
    }

    private Border Block(double height, Thickness margin, double radius) => new()
    {
        Tag = "bar",
        Height = height,
        Margin = margin,
        CornerRadius = new CornerRadius(radius),
        Background = _barBrush,
    };

    private UniformGrid BuildTiles()
    {
        var grid = new UniformGrid { Columns = 4, Rows = 2 };
        for (var i = 0; i < 8; i++)
        {
            grid.Children.Add(Block(height: 112, margin: new Thickness(0, 0, 16, 16), radius: 8));
        }

        return grid;
    }

    private StackPanel BuildPicker()
    {
        var panel = new StackPanel();
        for (var i = 0; i < 3; i++)
        {
            var row = new Grid { Height = 52, Margin = new Thickness(12, 0, 12, 0) };
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var title = Bar(width: 180, height: 12, align: HorizontalAlignment.Left);
            title.VerticalAlignment = VerticalAlignment.Bottom;
            title.Margin = new Thickness(0, 0, 0, 4);
            var subtitle = Bar(width: 120, height: 10, align: HorizontalAlignment.Left);
            subtitle.VerticalAlignment = VerticalAlignment.Top;
            subtitle.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(subtitle, 1);
            row.Children.Add(title);
            row.Children.Add(subtitle);
            panel.Children.Add(row);
        }

        return panel;
    }
}
