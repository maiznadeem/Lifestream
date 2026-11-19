using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Lifestream.Client.Features.Receipts;

namespace Lifestream.Desktop.Features.Receipts;

/// <summary>Shows a <see cref="ReceiptDocument"/> as the printable page (FEAT-18). The page is built by <see cref="ReceiptDocumentBuilder"/>, the same builder the printer uses.</summary>
public partial class ReceiptPrintView : UserControl
{
    public static readonly DependencyProperty ReceiptProperty =
        DependencyProperty.Register(nameof(Receipt), typeof(ReceiptDocument), typeof(ReceiptPrintView), new PropertyMetadata(null, (d, _) => ((ReceiptPrintView)d).Rebuild()));

    public ReceiptPrintView() => InitializeComponent();

    public ReceiptDocument? Receipt { get => (ReceiptDocument?)GetValue(ReceiptProperty); set => SetValue(ReceiptProperty, value); }

    /// <summary>The document being shown (for tests).</summary>
    public FlowDocument? Document => Viewer.Document;

    private void Rebuild() => Viewer.Document = Receipt is { } receipt ? ReceiptDocumentBuilder.Build(receipt) : new FlowDocument();
}
