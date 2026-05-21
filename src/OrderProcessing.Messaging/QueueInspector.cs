using System.Text;

namespace OrderProcessing.Messaging;

/// <summary>One parked message, as far as an operator needs to see it.</summary>
/// <param name="MessageId">The AMQP message id.</param>
/// <param name="CorrelationId">The id tying it back to the request that caused it.</param>
/// <param name="Attempts">How many attempts were made before it was parked.</param>
/// <param name="FailureReason">Why it was parked.</param>
/// <param name="OriginalRoutingKey">The key it arrived on.</param>
/// <param name="Body">The message body, so the order can be identified.</param>
public sealed record ParkedMessage(
    string? MessageId,
    string? CorrelationId,
    int Attempts,
    string? FailureReason,
    string? OriginalRoutingKey,
    string Body);

/// <summary>
/// Reads queue state without consuming it.
///
/// This exists because a dead-letter queue nobody can see is a dead-letter queue nobody empties. The
/// management UI can show it, but that means giving an operator broker credentials and expecting
/// them to know where to look; an endpoint on the service they already use is a lower bar.
/// </summary>
public interface IQueueInspector
{
    /// <summary>How many messages are sitting in <paramref name="queue"/>.</summary>
    /// <param name="queue">Queue name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<uint> GetDepthAsync(string queue, CancellationToken cancellationToken);

    /// <summary>
    /// Reads up to <paramref name="count"/> messages and puts them all back.
    /// </summary>
    /// <param name="queue">Queue name.</param>
    /// <param name="count">Maximum number of messages to look at.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<ParkedMessage>> PeekAsync(string queue, int count, CancellationToken cancellationToken);
}

internal sealed class QueueInspector(IRabbitMqConnection connection) : IQueueInspector
{
    public async Task<uint> GetDepthAsync(string queue, CancellationToken cancellationToken)
    {
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, cancellationToken)
            .ConfigureAwait(false);

        // Passive declaration asks about a queue without creating or altering it. Declaring it
        // actively here would risk a PRECONDITION_FAILED if this code's idea of the arguments ever
        // drifted from the real ones - an inspection endpoint must not be able to break anything.
        var ok = await channel.QueueDeclarePassiveAsync(queue, cancellationToken).ConfigureAwait(false);

        return ok.MessageCount;
    }

    public async Task<IReadOnlyList<ParkedMessage>> PeekAsync(string queue, int count, CancellationToken cancellationToken)
    {
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, cancellationToken)
            .ConfigureAwait(false);

        var found = new List<ParkedMessage>();
        var deliveryTags = new List<ulong>();

        try
        {
            for (var i = 0; i < count; i++)
            {
                var result = await channel.BasicGetAsync(queue, autoAck: false, cancellationToken)
                    .ConfigureAwait(false);

                if (result is null)
                {
                    break;
                }

                deliveryTags.Add(result.DeliveryTag);

                var headers = result.BasicProperties.Headers;
                found.Add(new ParkedMessage(
                    MessageId: result.BasicProperties.MessageId,
                    CorrelationId: result.BasicProperties.CorrelationId,
                    Attempts: ReadInt(headers, MessagingTopology.AttemptHeader),
                    FailureReason: ReadString(headers, MessagingTopology.FailureReasonHeader),
                    OriginalRoutingKey: ReadString(headers, MessagingTopology.OriginalRoutingKeyHeader),
                    Body: Encoding.UTF8.GetString(result.Body.Span)));
            }
        }
        finally
        {
            // Everything goes back, whatever happened. Reading a parked message must never be the
            // reason it stops being parked - this endpoint is a window, not a drain.
            foreach (var tag in deliveryTags)
            {
                // CancellationToken.None on purpose. If the caller has given up - a cancelled HTTP
                // request, a timeout - the messages must still go back. Honouring cancellation here
                // would mean an abandoned request could quietly consume the dead-letter queue.
                await channel.BasicNackAsync(tag, multiple: false, requeue: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return found;
    }

    private static string? ReadString(IDictionary<string, object?>? headers, string key) =>
        headers is not null && headers.TryGetValue(key, out var raw) && raw is not null
            ? raw switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => raw.ToString(),
            }
            : null;

    private static int ReadInt(IDictionary<string, object?>? headers, string key) =>
        int.TryParse(ReadString(headers, key), out var value) ? value : 0;
}
