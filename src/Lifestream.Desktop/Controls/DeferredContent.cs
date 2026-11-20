using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// Content that is built the first time it is laid out: its <see cref="Template"/> becomes its child when it is first measured, which never happens
/// while it, or an ancestor, is collapsed. A closed side panel holds its content this way, so showing a screen does not parse, style or bind the panel
/// it has not opened yet; the panel's first opening builds it, and it stays built. The content inherits the data context as any child does.
/// </summary>
/// <example>
/// <code>
/// &lt;controls:SidePanel IsOpen="{Binding IsDetailOpen}"&gt;
///   &lt;controls:DeferredContent&gt;&lt;DataTemplate&gt;&lt;Grid&gt;...&lt;/Grid&gt;&lt;/DataTemplate&gt;&lt;/controls:DeferredContent&gt;
/// &lt;/controls:SidePanel&gt;
/// </code>
/// </example>
[ContentProperty(nameof(Template))]
public sealed class DeferredContent : Decorator
{
    /// <summary>The content, built on first layout.</summary>
    public static readonly DependencyProperty TemplateProperty =
        DependencyProperty.Register(nameof(Template), typeof(DataTemplate), typeof(DeferredContent), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The content, built on first layout.</summary>
    public DataTemplate? Template { get => (DataTemplate?)GetValue(TemplateProperty); set => SetValue(TemplateProperty, value); }

    /// <summary>True once the content has been built.</summary>
    public bool IsBuilt => Child is not null;

    /// <summary>Raised once, right after the content has been built (before it is measured).</summary>
    public event EventHandler? Built;

    /// <summary>Builds the content now if it has not been built yet.</summary>
    public void EnsureBuilt()
    {
        if (Child is null && Template?.LoadContent() is UIElement content)
        {
            Child = content;
            Built?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size constraint)
    {
        EnsureBuilt();
        return base.MeasureOverride(constraint);
    }
}
