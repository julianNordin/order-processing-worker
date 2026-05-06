using OrderProcessing.Contracts;

namespace OrderProcessing.Messaging;

/// <summary>
/// Every exchange, queue and binding this system uses, described as data.
///
/// It is written down once, here, and declared by whichever process starts first — publisher or
/// consumer. That is the point: a publisher and a consumer that each declare their own half of the
/// topology can disagree about it, and when they do, nothing throws. The publish succeeds, the
/// broker finds no matching binding, and the message is dropped. One definition makes that
/// impossible.
///
/// The shape, and why it is this shape, is in <c>docs/topology.md</c>.
/// </summary>
public static class MessagingTopology
{
    /// <summary>Where orders are published. Direct, because routing here is an exact-match decision.</summary>
    public const string OrdersExchange = MessageContracts.Exchange;

    /// <summary>The queue the worker consumes.</summary>
    public const string OrdersPlacedQueue = "orders.placed";

    /// <summary>Where a transient failure sends a message to wait before being tried again.</summary>
    public const string RetryExchange = "orders.retry";

    /// <summary>
    /// Where a message goes when it will never succeed: malformed, unsupported schema version, or
    /// out of retries. Fanout rather than direct, deliberately — a dead letter that is itself lost
    /// to a routing-key mismatch is the worst possible outcome, and fanout cannot mis-route.
    /// </summary>
    public const string DeadLetterExchange = "orders.dlx";

    /// <summary>The parked queue. Nothing consumes it; a human decides what happens next.</summary>
    public const string DeadLetterQueue = "orders.dlq";

    /// <summary>
    /// The backoff ladder. Three separate queues rather than one queue with a per-message
    /// expiration, because RabbitMQ only expires messages at the HEAD of a queue: a message with a
    /// two-minute TTL sitting at the front holds back a five-second message behind it, however long
    /// that one has already waited. One queue per delay is the standard way round it.
    /// </summary>
    public static readonly IReadOnlyList<RetryTier> RetryTiers =
    [
        new RetryTier(Attempt: 1, Queue: "orders.retry.5s",  RoutingKey: "attempt.1", Delay: TimeSpan.FromSeconds(5)),
        new RetryTier(Attempt: 2, Queue: "orders.retry.30s", RoutingKey: "attempt.2", Delay: TimeSpan.FromSeconds(30)),
        new RetryTier(Attempt: 3, Queue: "orders.retry.2m",  RoutingKey: "attempt.3", Delay: TimeSpan.FromMinutes(2)),
    ];

    /// <summary>How many times a transient failure is retried before the message is parked.</summary>
    public static int MaxAttempts => RetryTiers.Count;

    /// <summary>
    /// The tier a message on its <paramref name="attempt"/>-th failure should wait in, or null when
    /// it has run out of attempts and belongs in the dead-letter queue.
    /// </summary>
    public static RetryTier? TierForAttempt(int attempt) =>
        RetryTiers.FirstOrDefault(t => t.Attempt == attempt);

    /// <summary>Header carrying how many times delivery of this message has already failed.</summary>
    public const string AttemptHeader = "x-attempt";

    /// <summary>Header explaining why a parked message was parked. Set on the way into the DLQ.</summary>
    public const string FailureReasonHeader = "x-failure-reason";

    /// <summary>Header preserving the routing key a parked message originally arrived on.</summary>
    public const string OriginalRoutingKeyHeader = "x-original-routing-key";
}

/// <summary>One rung of the backoff ladder.</summary>
/// <param name="Attempt">Which failure this tier handles: the first, second or third.</param>
/// <param name="Queue">The queue that holds the message while it waits.</param>
/// <param name="RoutingKey">The key that routes to <paramref name="Queue"/> on the retry exchange.</param>
/// <param name="Delay">How long the message waits, as the queue's message TTL.</param>
public sealed record RetryTier(int Attempt, string Queue, string RoutingKey, TimeSpan Delay);
