namespace OrderProcessing.Messaging;

/// <summary>
/// A message on its way to the broker, with the metadata that travels in the AMQP basic properties
/// rather than in the body.
/// </summary>
/// <param name="MessageId">
/// Uniquely identifies this message. It is the consumer's idempotency key, so it must survive a
/// retry unchanged — a retry that invents a new id defeats deduplication entirely.
/// </param>
/// <param name="CorrelationId">Ties the message back to the HTTP request that caused it.</param>
/// <param name="Type">Which contract the body holds, so a consumer can tell before parsing.</param>
/// <param name="Exchange">Where to publish.</param>
/// <param name="RoutingKey">How the exchange should route it.</param>
/// <param name="Body">The serialized payload.</param>
/// <param name="Headers">Application headers, such as the retry attempt count.</param>
public sealed record OutboundMessage(
    Guid MessageId,
    string CorrelationId,
    string Type,
    string Exchange,
    string RoutingKey,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, object?>? Headers = null);

/// <summary>
/// Thrown when the broker accepted a publish but could not route it to any queue.
///
/// This exists because the default behaviour is silence. Publish to an exchange with no matching
/// binding and RabbitMQ discards the message without error — the publisher sees success, the
/// consumer sees nothing, and there is no evidence anywhere that a message ever existed. Publishing
/// with <c>mandatory: true</c> turns that silence into this exception.
/// </summary>
public sealed class MessageNotRoutedException : Exception
{
    public MessageNotRoutedException(string exchange, string routingKey)
        : base($"The broker could not route a message to any queue: exchange '{exchange}', routing key '{routingKey}'. " +
               "The exchange exists but no binding matches this routing key.")
    {
        Exchange = exchange;
        RoutingKey = routingKey;
    }

    public MessageNotRoutedException()
    {
    }

    public MessageNotRoutedException(string message) : base(message)
    {
    }

    public MessageNotRoutedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public string? Exchange { get; }

    public string? RoutingKey { get; }
}
