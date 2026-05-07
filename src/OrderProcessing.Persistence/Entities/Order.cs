namespace OrderProcessing.Persistence.Entities;

/// <summary>
/// An order as the system stores it. Deliberately thin - this project is about the messaging around
/// the order, not about the order itself.
/// </summary>
public class Order
{
    public Guid Id { get; set; }

    public required string CustomerEmail { get; set; }

    /// <summary>
    /// The total as calculated when the order was accepted.
    ///
    /// Stored rather than derived from the lines on every read, because it is what the customer was
    /// told at the time. If a price changes tomorrow, the receipt must still say what they agreed to.
    /// </summary>
    public decimal Total { get; set; }

    public OrderStatus Status { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Why this order failed, when it did. Null otherwise.</summary>
    public string? FailureReason { get; set; }

    public List<OrderLine> Lines { get; } = [];
}
