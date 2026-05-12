using System.Globalization;
using OrderProcessing.Contracts;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OrderProcessing.Worker.Receipts;

/// <summary>
/// Renders an order into a receipt PDF.
///
/// This is the work that justifies doing any of it asynchronously. Laying out a document is real
/// CPU time — not much for one receipt, but enough that doing it inside the HTTP request would tie
/// up a request thread for something the customer is not waiting on. It is also the kind of work
/// that acquires dependencies over time (a template service, a logo from storage, a tax lookup),
/// each of which is another thing that can be slow or down while an order is being accepted.
/// </summary>
public interface IReceiptRenderer
{
    /// <summary>Renders <paramref name="order"/> as a PDF.</summary>
    /// <param name="order">The order the receipt is for.</param>
    /// <param name="generatedAt">The timestamp printed on the receipt.</param>
    byte[] Render(OrderPlaced order, DateTimeOffset generatedAt);
}

internal sealed class ReceiptRenderer : IReceiptRenderer
{
    public const string ContentType = "application/pdf";

    // Invariant, not the machine's culture. A receipt rendered on a Swedish host would otherwise use
    // a comma as the decimal separator and a different date order than one rendered elsewhere, so
    // the same order would produce different documents depending on which container picked it up.
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public byte[] Render(OrderPlaced order, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(order);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(text => text.FontSize(10).FontFamily(Fonts.Calibri));

                page.Header().Column(header =>
                {
                    header.Item().Text("Receipt").FontSize(22).SemiBold();
                    header.Item().PaddingTop(4).Text($"Order {order.OrderId}").FontSize(9).FontColor(Colors.Grey.Darken1);
                    header.Item().Text($"Issued {generatedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", Culture)}")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);
                    header.Item().PaddingTop(2).Text(order.CustomerEmail).FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                page.Content().PaddingVertical(20).Column(content =>
                {
                    content.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(70);
                            columns.RelativeColumn();
                            columns.ConstantColumn(45);
                            columns.ConstantColumn(70);
                            columns.ConstantColumn(75);
                        });

                        table.Header(head =>
                        {
                            head.Cell().Element(HeaderCell).Text("SKU");
                            head.Cell().Element(HeaderCell).Text("Description");
                            head.Cell().Element(HeaderCell).AlignRight().Text("Qty");
                            head.Cell().Element(HeaderCell).AlignRight().Text("Unit");
                            head.Cell().Element(HeaderCell).AlignRight().Text("Amount");
                        });

                        foreach (var line in order.Lines)
                        {
                            table.Cell().Element(BodyCell).Text(line.Sku);
                            table.Cell().Element(BodyCell).Text(line.Description);
                            table.Cell().Element(BodyCell).AlignRight().Text(line.Quantity.ToString(Culture));
                            table.Cell().Element(BodyCell).AlignRight().Text(line.UnitPrice.ToString("N2", Culture));
                            table.Cell().Element(BodyCell).AlignRight()
                                .Text((line.Quantity * line.UnitPrice).ToString("N2", Culture));
                        }
                    });

                    content.Item().PaddingTop(14).AlignRight().Text(text =>
                    {
                        text.Span("Total  ").SemiBold();
                        // The total from the message, not a sum recomputed here. If the two ever
                        // disagree that is a defect worth seeing, and it cannot be seen if only one
                        // side does the arithmetic.
                        text.Span(order.Total.ToString("N2", Culture)).FontSize(14).SemiBold();
                    });
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Generated asynchronously by OrderProcessing.Worker · schema v")
                        .FontSize(8).FontColor(Colors.Grey.Medium);
                    text.Span(order.SchemaVersion.ToString(Culture)).FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        }).GeneratePdf();
    }

    private static IContainer HeaderCell(IContainer container) => container
        .BorderBottom(1).BorderColor(Colors.Grey.Darken1)
        .PaddingVertical(4).DefaultTextStyle(text => text.SemiBold());

    private static IContainer BodyCell(IContainer container) => container
        .BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4);
}
