namespace OrderProcessing.Persistence.Entities;

/// <summary>
/// A message waiting to be published, written in the SAME transaction as the state change that
/// caused it.
///
/// This table exists to remove the dual write. The naive version of "save the order, then publish"
/// has two independent operations and no way to make them atomic: if the publish fails the order
/// exists but nobody will ever process it, and if the save fails after a successful publish the
/// worker processes an order that does not exist. Neither can be fixed with a try/catch, because the
/// process can die between the two lines.
///
/// Writing the message as a row makes it part of the same commit as the order. Either both exist or
/// neither does. A separate publisher then moves rows to the broker, and because that publisher can
/// crash and retry, the guarantee it provides is at-least-once - which is why the consumer has to be
/// idempotent.
/// </summary>
public class OutboxMessage
{
    /// <summary>
    /// Insertion order, and the order rows are published in.
    ///
    /// A sequence rather than the message id, because the id is a Guid and Guids do not sort by
    /// creation. Publishing out of order would be legal but confusing.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The AMQP message id. Unique, and the key the consumer deduplicates on, so a republish of the
    /// same row must reuse it rather than generate a fresh one.
    /// </summary>
    public Guid MessageId { get; set; }

    public required string CorrelationId { get; set; }

    /// <summary>Which contract <see cref="Payload"/> holds, for the AMQP type property.</summary>
    public required string MessageType { get; set; }

    public required string Exchange { get; set; }

    public required string RoutingKey { get; set; }

    /// <summary>The serialized message body, stored as jsonb so it can be queried when diagnosing.</summary>
    public required string Payload { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// When the broker confirmed it. Null until then - and null is what the publisher looks for.
    /// Set only AFTER a publisher confirm, never before, or the row would lie about being sent.
    /// </summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>How many times publishing this row has been attempted and failed.</summary>
    public int Attempts { get; set; }

    /// <summary>The last failure, kept so a stuck row can be explained without reproducing it.</summary>
    public string? LastError { get; set; }
}
