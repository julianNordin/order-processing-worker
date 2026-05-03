namespace OrderProcessing.Contracts;

/// <summary>
/// The names and versions both sides of the queue have to agree on, in one place so that they
/// cannot be spelled differently in the publisher and the consumer.
///
/// This matters more than it looks. A publisher that routes to "orders.placed" and a consumer bound
/// to "order.placed" produce no error at all: the publish succeeds, the broker finds no matching
/// binding, and the message is silently discarded. Constants make that particular typo impossible.
/// </summary>
public static class MessageContracts
{
    /// <summary>The exchange every order message is published to.</summary>
    public const string Exchange = "orders";

    /// <summary>The routing key for <see cref="OrderPlaced"/>.</summary>
    public const string OrderPlacedRoutingKey = "order.placed";

    /// <summary>
    /// Value for the AMQP <c>type</c> property, so a consumer can tell what a body is before
    /// attempting to parse it.
    /// </summary>
    public const string OrderPlacedMessageType = "OrderPlaced";

    public const string ContentType = "application/json";

    /// <summary>The schema version this build publishes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The versioning rule, stated once so that it is a decision rather than a habit.
    ///
    /// 1. Contracts are APPEND-ONLY. A new field may be added if it is optional and the message is
    ///    still meaningful without it. Nothing is ever renamed, retyped, or removed.
    /// 2. Consumers IGNORE fields they do not recognise. A message from a newer publisher must not
    ///    fail to deserialize on an older consumer, because during any rolling deploy that is
    ///    exactly what happens.
    /// 3. A change that breaks either of the above is NOT a version bump - it is a new routing key
    ///    (order.placed.v2) published alongside the old one until every consumer has moved.
    ///
    /// The reason for rule 3 is that a queue can hold messages published minutes or hours before the
    /// consumer that reads them was deployed. There is no moment at which both sides can be changed
    /// together, so a breaking change has to be additive at the transport level too.
    /// </summary>
    public const int MinimumSupportedSchemaVersion = 1;
}
