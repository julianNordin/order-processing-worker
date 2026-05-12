namespace OrderProcessing.Persistence.Entities;

/// <summary>
/// The generated receipt for an order. One per order, which is why the order id is the key.
///
/// The bytes live in the database rather than on a volume or in blob storage. That is the right call
/// for this project and the wrong one for a real system, and the README says so: a receipt is a few
/// kilobytes, keeping it here means the integration tests need no storage emulator and no volume
/// mount, and the transaction that writes the receipt is the same one that marks the order complete.
/// At production volume this column becomes the reason the database is expensive to back up.
/// </summary>
public class Receipt
{
    /// <summary>The order this receipt is for. Primary key, because there is exactly one.</summary>
    public Guid OrderId { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }

    public required string ContentType { get; set; }

    public required byte[] Content { get; set; }
}
