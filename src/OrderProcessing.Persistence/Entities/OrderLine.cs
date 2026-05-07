namespace OrderProcessing.Persistence.Entities;

/// <summary>
/// One line of a stored order.
///
/// Note this is a different type from OrderProcessing.Contracts.OrderLine, and deliberately so. The
/// contract is a wire format that other processes depend on and that may only change additively; this
/// is a storage shape that can be migrated whenever it suits. Collapsing the two into one type is
/// convenient right up to the first time a database change forces a breaking change on every consumer.
/// </summary>
public class OrderLine
{
    public long Id { get; set; }

    public Guid OrderId { get; set; }

    public required string Sku { get; set; }

    public required string Description { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }
}
