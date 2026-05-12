using System.Text;
using OrderProcessing.Contracts;
using OrderProcessing.Worker.Receipts;
using QuestPDF.Infrastructure;

namespace OrderProcessing.UnitTests;

/// <summary>
/// The receipt is the deliverable - the thing the whole asynchronous pipeline exists to produce - so
/// it is worth proving it renders at all, and that it renders the same way twice.
/// </summary>
public class ReceiptRendererTests
{
    static ReceiptRendererTests()
    {
        // QuestPDF throws on the first render if no licence has been declared, and the production
        // path sets this in Program.cs. Without it here, every test in this class fails with a
        // licensing exception that has nothing to do with what is being tested.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private static readonly DateTimeOffset GeneratedAt = new(2026, 5, 12, 9, 45, 0, TimeSpan.Zero);

    private static OrderPlaced AnOrder() => new()
    {
        SchemaVersion = MessageContracts.CurrentSchemaVersion,
        OrderId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"),
        CustomerEmail = "buyer@example.com",
        Total = 46.97m,
        Lines =
        [
            new OrderLine { Sku = "SKU-1", Description = "Blue widget", Quantity = 3, UnitPrice = 13.99m },
            new OrderLine { Sku = "SKU-2", Description = "Red widget", Quantity = 1, UnitPrice = 5.00m },
        ],
    };

    [Fact]
    public void Produces_something_that_is_actually_a_pdf()
    {
        var pdf = new ReceiptRenderer().Render(AnOrder(), GeneratedAt);

        // The magic bytes, not merely a non-empty array. An empty document and a broken one are both
        // byte arrays; only one of them starts with %PDF.
        Assert.True(pdf.Length > 1000, $"suspiciously small: {pdf.Length} bytes");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void Renders_an_order_with_many_lines_without_falling_over()
    {
        // Enough lines to spill onto a second page. Table layout across a page break is the most
        // likely thing to throw, and it will never happen with the two-line order in every other test.
        var order = AnOrder() with
        {
            Lines = [.. Enumerable.Range(1, 120).Select(i => new OrderLine
            {
                Sku = $"SKU-{i}",
                Description = $"Widget number {i} with a description long enough to wrap onto another line",
                Quantity = i,
                UnitPrice = 9.99m,
            })],
        };

        var pdf = new ReceiptRenderer().Render(order, GeneratedAt);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void Renders_an_order_with_a_zero_total()
    {
        // A fully discounted order is legitimate and must still produce a receipt.
        var order = AnOrder() with
        {
            Total = 0m,
            Lines = [new OrderLine { Sku = "FREE", Description = "Promotional item", Quantity = 1, UnitPrice = 0m }],
        };

        Assert.Equal("%PDF", Encoding.ASCII.GetString(new ReceiptRenderer().Render(order, GeneratedAt), 0, 4));
    }

    [Fact]
    public void Renders_the_same_bytes_for_the_same_order()
    {
        // Determinism matters more than it looks: the retry path can render the same order twice,
        // and two byte-different receipts for one order would be impossible to reconcile later.
        var renderer = new ReceiptRenderer();

        Assert.Equal(
            renderer.Render(AnOrder(), GeneratedAt),
            renderer.Render(AnOrder(), GeneratedAt));
    }
}
