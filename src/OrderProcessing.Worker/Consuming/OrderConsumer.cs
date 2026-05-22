using Microsoft.Extensions.Options;
using OrderProcessing.Contracts;
using OrderProcessing.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog.Context;

namespace OrderProcessing.Worker.Consuming;

/// <summary>
/// Consumes <c>orders.placed</c> and hands each message to <see cref="OrderPlacedHandler"/>.
///
/// Three settings define the safety of this loop, and all three are easy to get wrong in a way that
/// looks fine:
///
/// <b>autoAck: false.</b> With auto-acknowledgement the broker considers a message delivered the
/// instant it is written to the socket — before this process has looked at it. A crash mid-handler
/// then loses the message permanently, and nothing anywhere records that it existed.
///
/// <b>A real prefetch.</b> A prefetch of 0 means unlimited in AMQP, so the broker would hand this
/// consumer the entire queue and the process would hold all of it in memory. It also destroys
/// fairness: one consumer takes everything while its siblings idle.
///
/// <b>Catch everything in the handler.</b> An exception escaping the ReceivedAsync callback does
/// NOT nack the message. It is swallowed by the client, the delivery stays unacknowledged forever,
/// and it permanently occupies one of the prefetch slots — so a consumer that hits this bug a
/// prefetch-count number of times stops consuming altogether, silently, while appearing healthy.
/// </summary>
internal sealed class OrderConsumer(
    IRabbitMqConnection connection,
    IMessagePublisher publisher,
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<OrderConsumer> logger) : BackgroundService
{
    private readonly RabbitMqOptions _options = options.Value;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Most likely the queue does not exist yet, because the topology declaration is
                // still retrying against a broker that has not come up. Waiting and trying again is
                // the correct response; exiting would leave a healthy-looking process consuming
                // nothing.
                WorkerLog.ConsumerStartFailed(logger, ex);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ConsumeUntilStoppedAsync(CancellationToken stoppingToken)
    {
        _channel = await connection.CreateChannelAsync(publisherConfirms: false, stoppingToken)
            .ConfigureAwait(false);

        await _channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: _options.PrefetchCount,
            global: false,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;

        var tag = await _channel.BasicConsumeAsync(
            queue: MessagingTopology.OrdersPlacedQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        WorkerLog.ConsumerStarted(logger, MessagingTopology.OrdersPlacedQueue, _options.PrefetchCount, tag);

        // Nothing more to do on this thread; deliveries arrive on the client's own dispatcher.
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        var channel = _channel;
        if (channel is null)
        {
            return;
        }

        var messageId = delivery.BasicProperties.MessageId ?? "(none)";
        var correlationId = delivery.BasicProperties.CorrelationId ?? "(none)";
        var previousAttempts = RetryDecision.ReadAttempt(delivery.BasicProperties.Headers);

        // Everything logged for the rest of this delivery carries these, including anything the
        // handler logs and anything an exception is reported with. The correlation id came from the
        // HTTP request that placed the order, travelled through the outbox row and the AMQP
        // properties, and arrives here - so one query across both services returns the whole story
        // of one order rather than two disconnected halves.
        using var correlationScope = LogContext.PushProperty("CorrelationId", correlationId);
        using var messageScope = LogContext.PushProperty("MessageId", messageId);
        using var deliveryScope = LogContext.PushProperty("Redelivered", delivery.Redelivered);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<OrderPlacedHandler>();

            await handler.HandleAsync(
                Guid.TryParse(delivery.BasicProperties.MessageId, out var id) ? id : Guid.CreateVersion7(),
                delivery.Body,
                previousAttempts + 1,
                CancellationToken.None).ConfigureAwait(false);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var decision = RetryDecision.For(ex, previousAttempts);

            try
            {
                await ApplyAsync(channel, delivery, decision, ex).ConfigureAwait(false);
            }
            catch (Exception routingFailure)
            {
                // Could not even route the failure - the channel has probably gone. Leaving the
                // delivery unacknowledged is the right outcome, because the broker redelivers it
                // when the connection recovers. It has to be visible though: a message that quietly
                // stops being processed looks exactly like a message that was never sent.
                WorkerLog.FailureRoutingFailed(logger, routingFailure, messageId);
            }
        }
    }

    /// <summary>
    /// Acts on a <see cref="RetryDecision"/>: send the message round the backoff ladder, or park it.
    ///
    /// A retry is a PUBLISH followed by an ACK of the original, not a nack. Nacking would dead-letter
    /// the message immediately, with no way to delay it and no way to record which attempt this was.
    /// Publishing a copy to the retry exchange lets the wait queue's TTL provide the delay and lets
    /// the attempt counter travel with the message.
    ///
    /// The order matters. Publish first, then acknowledge: a crash in between means the original is
    /// redelivered and the work happens twice, which is a duplicate and survivable. The other order
    /// would mean a crash loses the message entirely, which is not.
    /// </summary>
    private async Task ApplyAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        RetryDecision decision,
        Exception failure)
    {
        var properties = delivery.BasicProperties;
        var messageId = properties.MessageId ?? "(none)";

        // The ORIGINAL message id is carried through, deliberately. It is the consumer's
        // deduplication key, and a retry that invented a fresh id would look like a different
        // message and defeat idempotency entirely.
        var id = Guid.TryParse(properties.MessageId, out var parsed) ? parsed : Guid.CreateVersion7();
        var correlationId = properties.CorrelationId ?? string.Empty;
        var type = properties.Type ?? MessageContracts.OrderPlacedMessageType;

        if (decision is { Action: FailureAction.Retry, Tier: not null })
        {
            WorkerLog.Retrying(logger, failure, messageId, decision.Attempt, decision.Tier.Delay);

            await publisher.PublishAsync(
                new OutboundMessage(
                    MessageId: id,
                    CorrelationId: correlationId,
                    Type: type,
                    Exchange: MessagingTopology.RetryExchange,
                    RoutingKey: decision.Tier.RoutingKey,
                    Body: delivery.Body,
                    Headers: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [MessagingTopology.AttemptHeader] = decision.Attempt,
                    }),
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            WorkerLog.Parking(logger, failure, messageId, decision.Attempt, decision.Reason);

            await publisher.PublishAsync(
                new OutboundMessage(
                    MessageId: id,
                    CorrelationId: correlationId,
                    Type: type,
                    Exchange: MessagingTopology.DeadLetterExchange,
                    RoutingKey: string.Empty,
                    Body: delivery.Body,
                    Headers: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [MessagingTopology.AttemptHeader] = decision.Attempt,
                        [MessagingTopology.FailureReasonHeader] = decision.Reason,
                        [MessagingTopology.OriginalRoutingKeyHeader] = delivery.RoutingKey,
                    }),
                CancellationToken.None).ConfigureAwait(false);
        }

        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_channel is not null)
        {
            await _channel.CloseAsync(cancellationToken).ConfigureAwait(false);
            await _channel.DisposeAsync().ConfigureAwait(false);
            _channel = null;
        }
    }
}
