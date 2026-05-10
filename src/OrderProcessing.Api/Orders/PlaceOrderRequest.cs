using System.ComponentModel.DataAnnotations;

namespace OrderProcessing.Api.Orders;

/// <summary>
/// What a caller sends to place an order.
///
/// Note what is NOT here: no order id, no total, no timestamp. The caller does not get to choose an
/// id (the server does, so it can guarantee uniqueness) and does not get to state a total (the
/// server computes it from the lines, so a client cannot claim a price).
/// </summary>
public sealed record PlaceOrderRequest
{
    [Required(AllowEmptyStrings = false)]
    [EmailAddress]
    [StringLength(320)]
    public string CustomerEmail { get; init; } = "";

    [Required]
    [MinLength(1, ErrorMessage = "An order must have at least one line.")]
    public IReadOnlyList<PlaceOrderLine> Lines { get; init; } = [];
}

public sealed record PlaceOrderLine
{
    [Required(AllowEmptyStrings = false)]
    [StringLength(64)]
    public string Sku { get; init; } = "";

    [Required(AllowEmptyStrings = false)]
    [StringLength(500)]
    public string Description { get; init; } = "";

    [Range(1, 10_000)]
    public int Quantity { get; init; }

    [Range(0.0, 1_000_000.0)]
    public decimal UnitPrice { get; init; }
}
