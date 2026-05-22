namespace OrderProcessing.Persistence.Entities;

/// <summary>
/// A record that one message has already been handled. The inbox half of the pattern whose outbox
/// half is <see cref="OutboxMessage"/>.
///
/// The broker offers at-least-once delivery and nothing stronger, and this system adds two more
/// sources of duplicates of its own: the outbox publisher can die between the broker's confirm and
/// its own commit, and the consumer publishes a retry before acknowledging the original. All three
/// are deliberate trades - each one chose "possibly twice" over "possibly never".
///
/// So the message will sometimes arrive twice, and no amount of care upstream prevents it. What can
/// be prevented is a SECOND RECEIPT. The effect is what gets made idempotent, not the delivery.
/// </summary>
public class ProcessedMessage
{
    /// <summary>
    /// The AMQP message id. Primary key - the whole mechanism is this uniqueness constraint.
    ///
    /// It has to be the database that enforces it rather than a "have I seen this?" query, because
    /// two concurrent deliveries of the same message would both find nothing and both proceed. A
    /// unique index is the only thing that can adjudicate between two transactions racing.
    /// </summary>
    public Guid MessageId { get; set; }

    public Guid OrderId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}
