using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace OrderProcessing.Messaging;

/// <summary>
/// Owns the single AMQP connection this process holds, and hands out channels on it.
///
/// One connection, many channels, is the shape RabbitMQ expects: connections are TCP sockets and
/// are expensive, channels are cheap multiplexed sessions over one. The opposite arrangement — a
/// connection per operation — is the most common way to exhaust a broker's file descriptors.
///
/// Channels are NOT thread-safe and are not shared here. Each caller gets its own.
/// </summary>
public interface IRabbitMqConnection : IAsyncDisposable
{
    /// <summary>
    /// Opens a channel, waiting for the broker to become reachable if it is not yet.
    /// </summary>
    /// <param name="publisherConfirms">
    /// Turn on for any channel that publishes something which must not be lost. It makes
    /// <c>BasicPublishAsync</c> await the broker's acknowledgement, which is the only way to know a
    /// publish actually landed — and the reason the outbox can mark a row sent without lying.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for a broker that is not yet reachable.</param>
    ValueTask<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken cancellationToken);
}

internal sealed class RabbitMqConnection(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConnection> logger) : IRabbitMqConnection
{
    private readonly RabbitMqOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async ValueTask<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Tracking is what makes the publish await an individual confirm rather than merely
        // enabling the feature; without it BasicPublishAsync returns before the broker has agreed.
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: publisherConfirms,
            publisherConfirmationTrackingEnabled: publisherConfirms);

        return await connection.CreateChannelAsync(channelOptions, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            _connection = await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Retries until the broker answers or the configured timeout expires.
    ///
    /// This exists for one specific situation: Compose starts the broker and the services at the
    /// same time, and the broker takes tens of seconds to accept AMQP. Without a wait here the
    /// worker dies on startup, the container restarts, and the real problem is buried under a
    /// restart loop. The client's own automatic recovery does not help — it reconnects a connection
    /// that once existed, and cannot rescue the first attempt.
    /// </summary>
    private async Task<IConnection> ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            ClientProvidedName = _options.ClientProvidedName,

            // Reconnect, and re-declare the topology on the recovered connection. Topology recovery
            // matters as much as the connection itself: a consumer that reconnects to a broker that
            // has forgotten its bindings is connected and deaf.
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
        };

        var deadline = DateTimeOffset.UtcNow + _options.ConnectionRetryTimeout;
        var delay = TimeSpan.FromSeconds(1);
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
                MessagingLog.Connected(logger, _options.Host, _options.Port, attempt);
                return connection;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    MessagingLog.ConnectionGaveUp(logger, ex, _options.Host, _options.Port, attempt);
                    throw;
                }

                MessagingLog.ConnectionAttemptFailed(logger, _options.Host, _options.Port, attempt, ex.Message, delay);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
