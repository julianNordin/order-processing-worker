using Microsoft.Extensions.Options;
using OrderProcessing.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

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

        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<OrderPlacedHandler>();

            await handler.HandleAsync(delivery.Body, CancellationToken.None).ConfigureAwait(false);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Phase 08 parks anything that fails. The retry ladder and the reason headers that make
            // a parked message diagnosable arrive in Phases 10 and 11; until then, requeue: false
            // sends it to the queue's dead-letter exchange rather than looping it back immediately,
            // which would spin the CPU on a message that fails instantly.
            WorkerLog.MessageFailed(logger, ex, messageId);

            try
            {
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false)
                    .ConfigureAwait(false);
            }
            catch (Exception nackFailure)
            {
                // The channel has probably gone. The delivery will be redelivered when the
                // connection recovers, which is the correct outcome - but it must be visible.
                WorkerLog.NackFailed(logger, nackFailure, messageId);
            }
        }
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
