using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Lifestream.Client.Features.Receipts;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Features.Receipts;

/// <summary>
/// Turns a <see cref="ReceiptDocument"/> into the FlowDocument that is shown in the receipt view, sent to the printer and saved as PDF (FEAT-18). The
/// page is paper: dark text on white, whatever the app's theme is, so a printed receipt looks right on any printer.
/// </summary>
public static class ReceiptDocumentBuilder
{
    public static FlowDocument Build(ReceiptDocument receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
            FontSize = 14,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            PagePadding = ThemeTokens.Inset("Print.Page"),
            ColumnWidth = double.PositiveInfinity,
            TextAlignment = TextAlignment.Left,
        };

        document.Blocks.Add(new Paragraph(new Run("Lifestream blood bank")) { FontSize = 12, Foreground = Brushes.DimGray, Margin = ThemeTokens.Inset("Inset.B4") });
        document.Blocks.Add(new Paragraph(new Run($"{receipt.Title} {receipt.Number}")) { FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = ThemeTokens.Inset("Inset.B16") });

        var table = new Table { CellSpacing = 0, Margin = ThemeTokens.Inset("Inset.B16") };
        var labelPadding = ThemeTokens.Inset("Print.CellLabel");
        var valuePadding = ThemeTokens.Inset("Print.CellValue");
        var ruled = ThemeTokens.Inset("Print.B1");
        table.Columns.Add(new TableColumn { Width = new GridLength(160) });
        table.Columns.Add(new TableColumn { Width = GridLength.Auto });
        var rows = new TableRowGroup();
        foreach (var line in receipt.Lines)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run(line.Label)) { Foreground = Brushes.DimGray }) { Padding = labelPadding, BorderBrush = Brushes.LightGray, BorderThickness = ruled });
            row.Cells.Add(new TableCell(new Paragraph(new Run(line.Value))) { Padding = valuePadding, BorderBrush = Brushes.LightGray, BorderThickness = ruled });
            rows.Rows.Add(row);
        }

        table.RowGroups.Add(rows);
        document.Blocks.Add(table);

        if (!string.IsNullOrWhiteSpace(receipt.Notes))
        {
            document.Blocks.Add(new Paragraph(new Run("Notes")) { Foreground = Brushes.DimGray, Margin = ThemeTokens.Inset("Print.B2") });
            document.Blocks.Add(new Paragraph(new Run(receipt.Notes)) { Margin = ThemeTokens.Inset("Inset.B16") });
        }

        if (!string.IsNullOrEmpty(receipt.CopyNotice))
        {
            document.Blocks.Add(new Paragraph(new Run(receipt.CopyNotice)) { FontSize = 12, FontStyle = FontStyles.Italic, Foreground = Brushes.DimGray });
        }

        return document;
    }
}
