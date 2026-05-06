using Microsoft.Extensions.Logging;
using OrderProcessing.Contracts;
using RabbitMQ.Client;

namespace OrderProcessing.Messaging;

/// <summary>
/// Declares the whole topology from <see cref="MessagingTopology"/>.
///
/// Declaration in AMQP is idempotent as long as the arguments match, so this runs unconditionally at
/// startup in both services and the second one to start is a no-op. What it is NOT tolerant of is a
/// change: redeclaring an existing queue with different arguments fails with PRECONDITION_FAILED and
/// takes the channel down with it. During development, changing a TTL means deleting the queue
/// first — which is what <c>scripts/rabbit-reset.ps1</c> is for.
/// </summary>
public sealed class TopologyDeclarer(IRabbitMqConnection connection, ILogger<TopologyDeclarer> logger)
{
    public async Task DeclareAsync(CancellationToken cancellationToken)
    {
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, cancellationToken)
            .ConfigureAwait(false);

        // --- the main path -------------------------------------------------------------------
        await channel.ExchangeDeclareAsync(
            MessagingTopology.OrdersExchange, ExchangeType.Direct,
            durable: true, autoDelete: false, arguments: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            MessagingTopology.OrdersPlacedQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                // A safety net, not the normal route. The consumer decides where a failed message
                // goes and publishes it there explicitly, because it needs to attach a reason. This
                // only catches rejections the broker itself originates.
                ["x-dead-letter-exchange"] = MessagingTopology.DeadLetterExchange,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            MessagingTopology.OrdersPlacedQueue, MessagingTopology.OrdersExchange,
            MessageContracts.OrderPlacedRoutingKey, arguments: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // --- the backoff ladder --------------------------------------------------------------
        await channel.ExchangeDeclareAsync(
            MessagingTopology.RetryExchange, ExchangeType.Direct,
            durable: true, autoDelete: false, arguments: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        foreach (var tier in MessagingTopology.RetryTiers)
        {
            await channel.QueueDeclareAsync(
                tier.Queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    // Nothing consumes these queues. A message sits here until its TTL expires, at
                    // which point the broker dead-letters it — and the dead-letter target is the
                    // main exchange, so expiry IS the retry.
                    ["x-message-ttl"] = (int)tier.Delay.TotalMilliseconds,
                    ["x-dead-letter-exchange"] = MessagingTopology.OrdersExchange,
                    ["x-dead-letter-routing-key"] = MessageContracts.OrderPlacedRoutingKey,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await channel.QueueBindAsync(
                tier.Queue, MessagingTopology.RetryExchange, tier.RoutingKey,
                arguments: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // --- the parked queue ----------------------------------------------------------------
        await channel.ExchangeDeclareAsync(
            MessagingTopology.DeadLetterExchange, ExchangeType.Fanout,
            durable: true, autoDelete: false, arguments: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            MessagingTopology.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            // Deliberately no TTL and no dead-letter target. A parked message stays parked until a
            // person decides what to do with it; expiring it would destroy the only evidence of why
            // it failed.
            arguments: null, cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            MessagingTopology.DeadLetterQueue, MessagingTopology.DeadLetterExchange,
            routingKey: string.Empty, arguments: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        MessagingLog.TopologyDeclared(logger, exchangeCount: 3, queueCount: 2 + MessagingTopology.RetryTiers.Count);
    }
}
