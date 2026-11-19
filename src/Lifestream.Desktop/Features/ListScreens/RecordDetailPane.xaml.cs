using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// The selected record in a detail panel: header, the actions (the screen's primary <see cref="RelatedActions"/>, Edit, and "..." with Delete), the screen's
/// <see cref="Body"/>, the record's facts and the screen's <see cref="Extra"/>.
/// </summary>
public partial class RecordDetailPane : UserControl
{
    public static readonly DependencyProperty RelatedActionsProperty = DependencyProperty.Register(nameof(RelatedActions), typeof(object), typeof(RecordDetailPane), new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(object), typeof(RecordDetailPane), new PropertyMetadata(null));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(nameof(Body), typeof(object), typeof(RecordDetailPane), new PropertyMetadata(null));

    public static readonly DependencyProperty ExtraProperty = DependencyProperty.Register(nameof(Extra), typeof(object), typeof(RecordDetailPane), new PropertyMetadata(null));

    public static readonly DependencyProperty FactsSourceProperty =
        DependencyProperty.Register(nameof(FactsSource), typeof(IEnumerable), typeof(RecordDetailPane), new PropertyMetadata(null));

    public static readonly DependencyProperty DeleteTextProperty = DependencyProperty.Register(nameof(DeleteText), typeof(string), typeof(RecordDetailPane), new PropertyMetadata("Delete"));

    public static readonly DependencyProperty ShowFrameProperty =
        DependencyProperty.Register(nameof(ShowFrame), typeof(bool), typeof(RecordDetailPane), new PropertyMetadata(true, (d, _) => ((RecordDetailPane)d).UpdateFrame()));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(RecordDetailPane), new PropertyMetadata(false, (d, e) => ((RecordDetailPane)d).EditText.Visibility = (bool)e.NewValue ? Visibility.Collapsed : Visibility.Visible));

    public RecordDetailPane()
    {
        InitializeComponent();
        UpdateFrame();
    }

    /// <summary>The screen's primary action on the record ("Book appointment" on a donor), before Edit.</summary>
    public object? RelatedActions { get => GetValue(RelatedActionsProperty); set => SetValue(RelatedActionsProperty, value); }

    /// <summary>The header (avatar, name, tag); the record's name when null.</summary>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <summary>Content between the actions and the facts (an eligibility card).</summary>
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    /// <summary>Content after the facts (a history).</summary>
    public object? Extra { get => GetValue(ExtraProperty); set => SetValue(ExtraProperty, value); }

    /// <summary>The facts to show; the view model's DetailFacts when not set.</summary>
    public IEnumerable? FactsSource { get => (IEnumerable?)GetValue(FactsSourceProperty); set => SetValue(FactsSourceProperty, value); }

    /// <summary>The "..." menu's delete item ("Delete donor").</summary>
    public string DeleteText { get => (string)GetValue(DeleteTextProperty); set => SetValue(DeleteTextProperty, value); }

    /// <summary>Draws the pane as its own panel (Layer, a 1 px line, rounded); off inside a side panel, which is the surface.</summary>
    public bool ShowFrame { get => (bool)GetValue(ShowFrameProperty); set => SetValue(ShowFrameProperty, value); }

    /// <summary>Edit shows only its icon (a narrow panel).</summary>
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FactsSourceProperty && Facts is not null && BindingOperations.GetBindingExpressionBase(Facts, FactGrid.FactsProperty) is null)
        {
            Facts.SetBinding(FactGrid.FactsProperty, new Binding(nameof(FactsSource)) { Source = this });
        }
    }

    private void UpdateFrame() => DetailPaneFrame.Apply(Frame, ShowFrame && !DetailPaneFrame.GetIsInPanel(this));

    internal void RefreshFrame() => UpdateFrame();
}

/// <summary>
/// The frame of a detail pane or record form outside a side panel: Layer, a 1 px Stroke, the panel radius and 20 padding. Inside a side panel (an ancestor sets
/// <see cref="IsInPanelProperty"/>, inherited) there is no frame: the panel is the surface.
/// </summary>
public static class DetailPaneFrame
{
    /// <summary>Set on a side panel: the panes and forms inside draw no frame of their own.</summary>
    public static readonly DependencyProperty IsInPanelProperty =
        DependencyProperty.RegisterAttached("IsInPanel", typeof(bool), typeof(DetailPaneFrame), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnIsInPanelChanged));

    public static bool GetIsInPanel(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(IsInPanelProperty);

    public static void SetIsInPanel(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(IsInPanelProperty, value);

    private static void OnIsInPanelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        switch (d)
        {
            case RecordDetailPane pane:
                pane.RefreshFrame();
                break;
            case ActionDetailPane pane:
                pane.RefreshFrame();
                break;
            case RecordEditorChrome chrome:
                chrome.RefreshFrame();
                break;
        }
    }

    internal static void Apply(Border frame, bool show)
    {
        if (show)
        {
            frame.SetResourceReference(Border.BackgroundProperty, "Brush.Layer");
            frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Stroke");
            frame.SetResourceReference(Border.PaddingProperty, "Pad.20");
            frame.SetResourceReference(Border.CornerRadiusProperty, "Radius.Panel");
            frame.SetResourceReference(Border.BorderThicknessProperty, "Pad.1");
        }
        else
        {
            frame.ClearValue(Border.BackgroundProperty);
            frame.ClearValue(Border.BorderBrushProperty);
            frame.ClearValue(Border.PaddingProperty);
            frame.ClearValue(Border.CornerRadiusProperty);
            frame.ClearValue(Border.BorderThicknessProperty);
        }
    }
}
