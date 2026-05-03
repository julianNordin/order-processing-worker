namespace OrderProcessing.Contracts;

/// <summary>
/// One line of an order. Both the description and the unit price are carried rather than looked up
/// by the worker, because a receipt has to record what the customer was told at the time they
/// ordered - not what the catalogue says by the time the receipt is generated.
/// </summary>
public sealed record OrderLine
{
    public required string Sku { get; init; }

    public required string Description { get; init; }

    public required int Quantity { get; init; }

    public required decimal UnitPrice { get; init; }
}
