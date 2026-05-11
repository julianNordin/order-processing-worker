using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderProcessing.Messaging;
using OrderProcessing.Persistence;
using OrderProcessing.Persistence.Entities;

namespace OrderProcessing.Api.Outbox;

/// <summary>
/// Moves rows from <c>outbox_messages</c> to the broker.
///
/// This is the half of the outbox pattern that makes the other half safe. The request handler writes
/// the order and the message in one transaction and returns; this service, entirely separately,
/// takes responsibility for getting the message to the broker eventually. "Eventually" is the
/// operative word — if the broker is down, the rows accumulate and the customer still gets their
/// 202, which is exactly the behaviour the pattern exists to produce.
///
/// <b>The duplicate window, stated plainly.</b> A row is marked published only after the broker
/// confirms, but the mark and the confirm are not one atomic act: the process can die after the
/// broker has accepted a message and before the transaction commits. The row is then still unsent,
/// and it will be published again. That is a deliberate trade — losing a message is unacceptable,
/// sending one twice is merely inconvenient, and the consumer is made idempotent in Phase 12 to
/// absorb it. At-least-once is a choice here, not an accident.
/// </summary>
internal sealed class OutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IMessagePublisher publisher,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int published;
            try
            {
                published = await PublishBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The loop must survive anything - a database that has gone away, a broker that has
                // gone away, a bug. A publisher that exits on the first exception is a publisher
                // that silently stops delivering while the API keeps accepting orders.
                OutboxLog.BatchFailed(logger, ex);
                published = 0;
            }

            // A full batch means there is probably more waiting, so go straight round again rather
            // than sleeping through a backlog.
            var delay = published >= _options.BatchSize ? TimeSpan.Zero : _options.PollInterval;

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>();

        await using var transaction = await database.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // FOR UPDATE SKIP LOCKED is what lets more than one API instance drain this table at once.
        // The rows this instance selects are locked for the life of the transaction, and a second
        // instance running the same query steps over them rather than blocking on them - so two
        // publishers share the work instead of serialising, and neither can publish the same row.
        //
        // Without SKIP LOCKED the second instance would wait for the first transaction to finish,
        // turning two publishers into one publisher and a queue of blocked ones.
        var batch = await database.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE published_at IS NULL
                ORDER BY id
                LIMIT {_options.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (batch.Count == 0)
        {
            return 0;
        }

        // Bound the whole batch's dealings with the broker. See OutboxOptions.PublishTimeout for
        // why this cannot be left to the connection layer's own patience.
        using var publishWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        publishWindow.CancelAfter(_options.PublishTimeout);

        foreach (var row in batch)
        {
            await PublishRowAsync(row, publishWindow.Token, cancellationToken).ConfigureAwait(false);
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return batch.Count;
    }

    /// <param name="publishToken">Bounded window for reaching the broker.</param>
    /// <param name="shutdownToken">
    /// The service's own token. Distinguishing the two matters: a timeout is a failure worth
    /// recording against the row, whereas a shutdown is not the row's fault and must not inflate its
    /// attempt count.
    /// </param>
    /// <param name="row">The outbox row to publish.</param>
    private async Task PublishRowAsync(OutboxMessage row, CancellationToken publishToken, CancellationToken shutdownToken)
    {
        try
        {
            await publisher.PublishAsync(
                new OutboundMessage(
                    MessageId: row.MessageId,
                    CorrelationId: row.CorrelationId,
                    Type: row.MessageType,
                    Exchange: row.Exchange,
                    RoutingKey: row.RoutingKey,
                    Body: Encoding.UTF8.GetBytes(row.Payload)),
                publishToken).ConfigureAwait(false);

            row.PublishedAt = clock.GetUtcNow();
            row.LastError = null;

            OutboxLog.Published(logger, row.MessageId, row.RoutingKey);
        }
        catch (Exception ex) when (!shutdownToken.IsCancellationRequested)
        {
            // The row keeps published_at null, so the next pass tries again. Recording the attempt
            // count and the reason is what makes a permanently stuck row explainable without
            // having to reproduce the failure.
            row.Attempts++;

            // "A task was canceled" is what a timeout reports, and it explains nothing to whoever
            // reads this column later. Say what actually happened instead.
            row.LastError = Truncate(
                ex is OperationCanceledException
                    ? $"Timed out after {_options.PublishTimeout.TotalSeconds:0.#}s trying to reach the broker."
                    : $"{ex.GetType().Name}: {ex.Message}",
                2000);

            OutboxLog.PublishFailed(logger, ex, row.MessageId, row.Attempts);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
