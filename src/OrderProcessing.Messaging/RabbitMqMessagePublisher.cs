using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderProcessing.Messaging;

/// <summary>
/// Publishes a message and does not return until the broker has confirmed it was both accepted and
/// routed. Anything less would let the outbox mark a row as sent when it was not.
/// </summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Publishes one message, waiting for the broker's acknowledgement.
    /// </summary>
    /// <param name="message">The message to publish.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <exception cref="MessageNotRoutedException">
    /// The broker accepted the message but no queue was bound to receive it.
    /// </exception>
    Task PublishAsync(OutboundMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Holds one confirming channel and publishes on it.
///
/// Two settings do the work here, and both exist to convert a silent failure into a loud one:
///
/// <b>Publisher confirms</b> make <c>BasicPublishAsync</c> wait for the broker to say it has taken
/// responsibility for the message. Without them the call returns as soon as the bytes are written to
/// the socket, which says nothing about whether the broker ever received them — a broker that dies
/// mid-publish looks identical to a successful one.
///
/// <b>mandatory: true</b> makes the broker return a message it cannot route to any queue. Without
/// it, publishing to a valid exchange with a routing key nothing is bound to succeeds and the
/// message is discarded. That single behaviour is the most common way a RabbitMQ system loses
/// messages while appearing to work.
/// </summary>
internal sealed class RabbitMqMessagePublisher(
    IRabbitMqConnection connection,
    ILogger<RabbitMqMessagePublisher> logger) : IMessagePublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    /// <summary>
    /// Message ids the broker has handed back as unroutable.
    ///
    /// AMQP delivers basic.return BEFORE the basic.ack for the same message, so by the time the
    /// awaited publish completes, a return for it has already arrived and been recorded here.
    /// </summary>
    private readonly HashSet<string> _returned = [];

    public async Task PublishAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var channel = await GetChannelAsync(cancellationToken).ConfigureAwait(false);
        var messageId = message.MessageId.ToString();

        var properties = new BasicProperties
        {
            MessageId = messageId,
            CorrelationId = message.CorrelationId,
            Type = message.Type,
            ContentType = "application/json",
            ContentEncoding = "utf-8",

            // Without this the broker keeps the message in memory only, and a restart loses every
            // message sitting in a durable queue. Durable queue plus transient message is a
            // combination that looks safe and is not.
            DeliveryMode = DeliveryModes.Persistent,
        };

        if (message.Headers is { Count: > 0 })
        {
            properties.Headers = message.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal);
        }

        lock (_returned)
        {
            _returned.Remove(messageId);
        }

        await channel.BasicPublishAsync(
            exchange: message.Exchange,
            routingKey: message.RoutingKey,
            mandatory: true,
            basicProperties: properties,
            body: message.Body,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        bool wasReturned;
        lock (_returned)
        {
            wasReturned = _returned.Remove(messageId);
        }

        if (wasReturned)
        {
            MessagingLog.MessageNotRouted(logger, message.Exchange, message.RoutingKey, message.MessageId);
            throw new MessageNotRoutedException(message.Exchange, message.RoutingKey);
        }
    }

    private async ValueTask<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            _channel = await connection.CreateChannelAsync(publisherConfirms: true, cancellationToken)
                .ConfigureAwait(false);

            _channel.BasicReturnAsync += OnBasicReturnAsync;
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task OnBasicReturnAsync(object sender, BasicReturnEventArgs args)
    {
        var messageId = args.BasicProperties?.MessageId;

        if (!string.IsNullOrEmpty(messageId))
        {
            lock (_returned)
            {
                _returned.Add(messageId);
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            _channel.BasicReturnAsync -= OnBasicReturnAsync;
            await _channel.CloseAsync().ConfigureAwait(false);
            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
