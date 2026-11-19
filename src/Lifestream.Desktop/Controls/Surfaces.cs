using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lifestream.Client.Common;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// Shows exactly one of four things in one cell (SHELL-28): a skeleton while loading, the real content when ready, an
/// empty state, or an error state. Because they share a cell, the skeleton is replaced in place and nothing shifts.
/// While the server is waking up it shows the skeleton plus a "Waking up the server" banner with Cancel.
/// </summary>
public sealed class LoadSurface : ContentControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(LoadState), typeof(LoadSurface), new FrameworkPropertyMetadata(LoadState.Ready));
    public static readonly DependencyProperty SkeletonKindProperty = DependencyProperty.Register(nameof(SkeletonKind), typeof(SkeletonKind), typeof(LoadSurface), new FrameworkPropertyMetadata(SkeletonKind.List));
    public static readonly DependencyProperty SkeletonColumnsProperty = DependencyProperty.Register(nameof(SkeletonColumns), typeof(string), typeof(LoadSurface), new FrameworkPropertyMetadata("2*,*,*,*"));
    public static readonly DependencyProperty ErrorMessageProperty = DependencyProperty.Register(nameof(ErrorMessage), typeof(string), typeof(LoadSurface), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty ErrorDetailProperty = DependencyProperty.Register(nameof(ErrorDetail), typeof(string), typeof(LoadSurface), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(nameof(RetryCommand), typeof(ICommand), typeof(LoadSurface), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(nameof(CancelCommand), typeof(ICommand), typeof(LoadSurface), new FrameworkPropertyMetadata(null, (d, _) => ((LoadSurface)d).BindCancel()));
    public static readonly DependencyProperty EmptyGlyphProperty = DependencyProperty.Register(nameof(EmptyGlyph), typeof(string), typeof(LoadSurface), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty EmptyTitleProperty = DependencyProperty.Register(nameof(EmptyTitle), typeof(string), typeof(LoadSurface), new FrameworkPropertyMetadata("Nothing here yet"));
    public static readonly DependencyProperty EmptyMessageProperty = DependencyProperty.Register(nameof(EmptyMessage), typeof(string), typeof(LoadSurface), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty EmptyActionTextProperty = DependencyProperty.Register(nameof(EmptyActionText), typeof(string), typeof(LoadSurface), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty EmptyActionCommandProperty = DependencyProperty.Register(nameof(EmptyActionCommand), typeof(ICommand), typeof(LoadSurface), new FrameworkPropertyMetadata(null));

    public LoadState State { get => (LoadState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    public SkeletonKind SkeletonKind { get => (SkeletonKind)GetValue(SkeletonKindProperty); set => SetValue(SkeletonKindProperty, value); }

    /// <summary>The same column spec as the grid on this surface, so the list skeleton lines up with it.</summary>
    public string SkeletonColumns { get => (string)GetValue(SkeletonColumnsProperty); set => SetValue(SkeletonColumnsProperty, value); }

    public string? ErrorMessage { get => (string?)GetValue(ErrorMessageProperty); set => SetValue(ErrorMessageProperty, value); }

    public string? ErrorDetail { get => (string?)GetValue(ErrorDetailProperty); set => SetValue(ErrorDetailProperty, value); }

    public ICommand? RetryCommand { get => (ICommand?)GetValue(RetryCommandProperty); set => SetValue(RetryCommandProperty, value); }

    public ICommand? CancelCommand { get => (ICommand?)GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }

    public string EmptyGlyph { get => (string)GetValue(EmptyGlyphProperty); set => SetValue(EmptyGlyphProperty, value); }

    public string EmptyTitle { get => (string)GetValue(EmptyTitleProperty); set => SetValue(EmptyTitleProperty, value); }

    public string? EmptyMessage { get => (string?)GetValue(EmptyMessageProperty); set => SetValue(EmptyMessageProperty, value); }

    public string? EmptyActionText { get => (string?)GetValue(EmptyActionTextProperty); set => SetValue(EmptyActionTextProperty, value); }

    public ICommand? EmptyActionCommand { get => (ICommand?)GetValue(EmptyActionCommandProperty); set => SetValue(EmptyActionCommandProperty, value); }

    /// <summary>The skeleton part of the template (for tests).</summary>
    public Skeleton? SkeletonPart => GetTemplateChild("PART_Skeleton") as Skeleton;

    public LoadSurface() => DataContextChanged += (_, _) => BindCancel();

    /// <summary>
    /// The banner's Cancel runs <see cref="CancelCommand"/> when a view sets one, and otherwise cancels the load of the
    /// screen's own view model, so Cancel works wherever the banner appears (SHELL-07).
    /// </summary>
    private void BindCancel()
    {
        if (GetTemplateChild("PART_CancelWaking") is Button cancel)
        {
            cancel.Command = CancelCommand ?? (DataContext as ViewModelBase)?.CancelLoadCommand;
        }
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        BindCancel();
        if (GetTemplateChild("PART_WakingBanner") is UIElement banner)
        {
            AutomationProperties.SetLiveSetting(banner, AutomationLiveSetting.Polite);
        }
    }
}

/// <summary>A plain-language empty state: icon, one-line cause, and an action (Add, Clear filters).</summary>
public sealed class EmptyState : Control
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new FrameworkPropertyMetadata(string.Empty));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(EmptyState), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(EmptyState), new FrameworkPropertyMetadata(null));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }

    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
}

/// <summary>
/// A plain-language error state (offline, server waking up, permission, unknown) with Retry; the technical detail sits behind a
/// "Details" control and can be copied. A raw exception never appears in the main text.
/// </summary>
public sealed class ErrorState : Control
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(ErrorState), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(ErrorState), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(nameof(RetryCommand), typeof(ICommand), typeof(ErrorState), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty IsDetailOpenProperty = DependencyProperty.Register(nameof(IsDetailOpen), typeof(bool), typeof(ErrorState), new FrameworkPropertyMetadata(false));

    public ErrorState() => CopyDetailCommand = new RoutedCommand();

    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    public string? Detail { get => (string?)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    public ICommand? RetryCommand { get => (ICommand?)GetValue(RetryCommandProperty); set => SetValue(RetryCommandProperty, value); }

    public bool IsDetailOpen { get => (bool)GetValue(IsDetailOpenProperty); set => SetValue(IsDetailOpenProperty, value); }

    public ICommand CopyDetailCommand { get; }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        CommandBindings.Clear();
        CommandBindings.Add(new CommandBinding(CopyDetailCommand, (_, _) =>
        {
            try
            {
                Clipboard.SetText(Detail ?? string.Empty);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // The clipboard can be locked by another process; the detail stays selectable in the box.
            }
        }));
    }
}

/// <summary>The statuses the chip knows. Each has an icon, text and a token colour, so meaning never depends on colour alone (SHELL-40).</summary>
public enum StatusKind
{
    Pending,
    Completed,
    Cancelled,
    Incomplete,
    LowStock,
    Expiring,
}

/// <summary>A status chip: icon + text + token colour.</summary>
public sealed class StatusChip : Control
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(StatusKind), typeof(StatusChip), new FrameworkPropertyMetadata(StatusKind.Pending, OnStatusChanged));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(StatusChip), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty DisplayTextProperty = DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(StatusChip), new FrameworkPropertyMetadata("Pending"));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(StatusChip), new FrameworkPropertyMetadata(null));

    /// <summary>Replaces the chip's text when the record's own word is more exact ("No show", "Fulfilled"); the icon and colour stay those of <see cref="Status"/>.</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatusChip), new FrameworkPropertyMetadata(null, OnStatusChanged));

    public StatusChip()
    {
        Loaded += (_, _) => Apply();
        Focusable = false;
    }

    public StatusKind Status { get => (StatusKind)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string Glyph { get => (string)GetValue(GlyphProperty); private set => SetValue(GlyphProperty, value); }

    public string DisplayText { get => (string)GetValue(DisplayTextProperty); private set => SetValue(DisplayTextProperty, value); }

    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); private set => SetValue(AccentBrushProperty, value); }

    /// <summary>The icon, text and token key of each status.</summary>
    public static (string Glyph, string Text, string BrushKey) Describe(StatusKind status) => status switch
    {
        StatusKind.Pending => ("", "Pending", "Brush.StatusInfo"),
        StatusKind.Completed => ("", "Completed", "Brush.StatusSuccess"),
        StatusKind.Cancelled => ("", "Cancelled", "Brush.StatusDanger"),
        StatusKind.Incomplete => ("", "Incomplete", "Brush.StatusWarning"),
        StatusKind.LowStock => ("", "Low stock", "Brush.StatusDanger"),
        StatusKind.Expiring => ("", "Expiring", "Brush.StatusWarning"),
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((StatusChip)d).Apply();

    private void Apply()
    {
        var (glyph, text, key) = Describe(Status);
        Glyph = glyph;
        DisplayText = string.IsNullOrEmpty(Label) ? text : Label;
        AccentBrush = TryFindResource(key) as Brush;
        AutomationProperties.SetName(this, DisplayText);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}

/// <summary>
/// A form field: a persistent label above the input, an optional hint, and an error message with an icon below it (SHELL-32,
/// SHELL-33). The label is associated with the input for UI Automation, and a new error is announced through a live region.
/// </summary>
public sealed class FormField : ContentControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(FormField), new FrameworkPropertyMetadata(string.Empty, OnLabelChanged));
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(FormField), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty ErrorProperty = DependencyProperty.Register(nameof(Error), typeof(string), typeof(FormField), new FrameworkPropertyMetadata(null, OnErrorChanged));
    public static readonly DependencyProperty IsRequiredProperty = DependencyProperty.Register(nameof(IsRequired), typeof(bool), typeof(FormField), new FrameworkPropertyMetadata(false));
    private static readonly DependencyPropertyKey HasErrorPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasError), typeof(bool), typeof(FormField), new FrameworkPropertyMetadata(false));

    /// <summary>True while an error message is shown; the hint gives way to it.</summary>
    public static readonly DependencyProperty HasErrorProperty = HasErrorPropertyKey.DependencyProperty;

    public FormField()
    {
        Focusable = false;
        Loaded += (_, _) => AssociateLabel();
    }

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string? Hint { get => (string?)GetValue(HintProperty); set => SetValue(HintProperty, value); }

    public string? Error { get => (string?)GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }

    public bool IsRequired { get => (bool)GetValue(IsRequiredProperty); set => SetValue(IsRequiredProperty, value); }

    public bool HasError => (bool)GetValue(HasErrorProperty);

    /// <summary>The input control inside the field (the first focusable descendant of the content).</summary>
    public UIElement? Input => Content is UIElement { Focusable: true } direct ? direct : FindFocusable(Content as DependencyObject);

    /// <summary>How many times focus was requested for this field (the first invalid field is asked on submit).</summary>
    public int FocusRequestCount { get; private set; }

    /// <summary>Moves keyboard focus to the input; used to jump to the first invalid field.</summary>
    public bool FocusInput()
    {
        FocusRequestCount++;
        return Input?.Focus() ?? false;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        AssociateLabel();
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((FormField)d).AssociateLabel();

    private static void OnErrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (FormField)d;
        field.SetValue(HasErrorPropertyKey, e.NewValue is string { Length: > 0 });
        field.AssociateLabel();
        if (e.NewValue is string { Length: > 0 } && field.GetTemplateChild("PART_Error") is UIElement error)
        {
            // Announce the message to screen readers the moment it appears.
            UIElementAutomationPeer.FromElement(error)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void AssociateLabel()
    {
        if (Input is not { } input)
        {
            return;
        }

        if (string.IsNullOrEmpty(AutomationProperties.GetName(input)))
        {
            AutomationProperties.SetName(input, Label);
        }

        if (GetTemplateChild("PART_Label") is UIElement label)
        {
            AutomationProperties.SetLabeledBy(input, label);
        }

        AutomationProperties.SetIsRequiredForForm(input, IsRequired);
        AutomationProperties.SetHelpText(input, string.IsNullOrEmpty(Error) ? Hint ?? string.Empty : Error);
    }

    private static UIElement? FindFocusable(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Control { Focusable: true, IsEnabled: true } element)
            {
                return element;
            }

            if (FindFocusable(child) is { } nested)
            {
                return nested;
            }
        }

        foreach (var logical in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (logical is Control { Focusable: true } control)
            {
                return control;
            }

            if (FindFocusable(logical) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

/// <summary>
/// A glyph from the system icon font. It is decorative: it has no automation peer, so screen readers do not read out a
/// private-use character. The control it sits in (a button, a chip) carries the accessible name.
/// </summary>
public sealed class IconText : TextBlock
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}

/// <summary>
/// Connects a form view model to its fields (SHELL-32): <c>controls:FormFocus.FieldName="Name"</c> on a <see cref="FormField"/>
/// names it, and <c>controls:FormFocus.IsEnabled="True"</c> on the form's root makes the first invalid field take focus when the view
/// model asks for it on submit.
/// </summary>
public static class FormFocus
{
    public static readonly DependencyProperty FieldNameProperty =
        DependencyProperty.RegisterAttached("FieldName", typeof(string), typeof(FormFocus), new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(FormFocus), new PropertyMetadata(false, OnEnabledChanged));

    public static string? GetFieldName(DependencyObject element) => (string?)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(FieldNameProperty);

    public static void SetFieldName(DependencyObject element, string? value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(FieldNameProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(IsEnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement root)
        {
            return;
        }

        root.DataContextChanged -= OnDataContextChanged;
        if ((bool)e.NewValue)
        {
            root.DataContextChanged += OnDataContextChanged;
            Attach(root, root.DataContext as Lifestream.Client.Validation.FormViewModelBase);
        }
    }

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is Lifestream.Client.Validation.FormViewModelBase old)
        {
            old.FocusRequested -= Handlers[(FrameworkElement)sender];
        }

        Attach((FrameworkElement)sender, e.NewValue as Lifestream.Client.Validation.FormViewModelBase);
    }

    private static readonly Dictionary<FrameworkElement, EventHandler<string>> Handlers = [];

    private static void Attach(FrameworkElement root, Lifestream.Client.Validation.FormViewModelBase? form)
    {
        if (form is null)
        {
            return;
        }

        void Focus(object? sender, string field)
        {
            var target = FindFields(root).FirstOrDefault(f => GetFieldName(f) == field);
            target?.FocusInput();
        }

        Handlers[root] = Focus;
        form.FocusRequested += Focus;
    }

    private static IEnumerable<FormField> FindFields(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is FormField field)
            {
                yield return field;
            }

            foreach (var inner in FindFields(child))
            {
                yield return inner;
            }
        }
    }
}

/// <summary>
/// A chart with its equivalent data table (SHELL-35): a toggle switches between the chart and a data grid with the same numbers.
/// The frame carries the accessible name; the table is the readable form of the chart for screen readers.
/// </summary>
public sealed class ChartFrame : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ChartFrame), new FrameworkPropertyMetadata(string.Empty, (d, _) => ((ChartFrame)d).UpdateName()));
    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(nameof(Table), typeof(ChartTable), typeof(ChartFrame), new FrameworkPropertyMetadata(null, (d, _) => ((ChartFrame)d).RebuildTable()));
    public static readonly DependencyProperty IsTableShownProperty = DependencyProperty.Register(nameof(IsTableShown), typeof(bool), typeof(ChartFrame), new FrameworkPropertyMetadata(false));

    public ChartFrame() => Focusable = false;

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public ChartTable? Table { get => (ChartTable?)GetValue(TableProperty); set => SetValue(TableProperty, value); }

    public bool IsTableShown { get => (bool)GetValue(IsTableShownProperty); set => SetValue(IsTableShownProperty, value); }

    /// <summary>The table grid of the template (for tests).</summary>
    public LifestreamDataGrid? TableGrid => GetTemplateChild("PART_Table") as LifestreamDataGrid;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateName();
        RebuildTable();
    }

    private void UpdateName() => AutomationProperties.SetName(this, string.IsNullOrEmpty(Title) ? "Chart" : $"{Title} chart");

    private void RebuildTable()
    {
        if (TableGrid is not { } grid)
        {
            return;
        }

        grid.Columns.Clear();
        grid.ItemsSource = null;
        if (Table is not { } table)
        {
            return;
        }

        for (var i = 0; i < table.Columns.Count; i++)
        {
            grid.Columns.Add(new DataGridTextColumn { Header = table.Columns[i], Binding = new System.Windows.Data.Binding($"[{i}]"), Width = i == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto });
        }

        grid.ItemsSource = table.Rows;
        AutomationProperties.SetHelpText(this, $"Data table with {table.Rows.Count} rows and {table.Columns.Count} columns.");
    }
}
