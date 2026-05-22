using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using OrderProcessing.Contracts;
using OrderProcessing.Persistence;
using OrderProcessing.Persistence.Entities;
using OrderProcessing.Worker.Receipts;
using Serilog.Context;

namespace OrderProcessing.Worker.Consuming;

/// <summary>
/// Raised for a message that will never succeed however many times it is delivered: a body that is
/// not valid JSON, a schema version this build does not understand, an order that does not exist.
///
/// Distinguishing this from an ordinary failure is the single most useful decision in the whole
/// consumer. Retrying a malformed message wastes the retry budget and delays the queue behind it
/// for minutes, and at the end of it the message is still malformed.
/// </summary>
public sealed class PermanentMessageFailureException : Exception
{
    public PermanentMessageFailureException(string message) : base(message)
    {
    }

    public PermanentMessageFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PermanentMessageFailureException()
    {
    }
}

/// <summary>
/// Does the actual work for one <see cref="OrderPlaced"/> message: render the receipt, store it,
/// and mark the order complete.
/// </summary>
internal sealed class OrderPlacedHandler(
    OrderProcessingDbContext database,
    IReceiptRenderer renderer,
    IOptions<FaultInjectionOptions> faults,
    TimeProvider clock,
    ILogger<OrderPlacedHandler> logger)
{
    private readonly FaultInjectionOptions _faults = faults.Value;

    /// <param name="messageId">
    /// The AMQP message id, which is the deduplication key. A retry reuses it deliberately.
    /// </param>
    /// <param name="body">The raw message body.</param>
    /// <param name="attempt">Which delivery attempt this is, starting at 1.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task HandleAsync(
        Guid messageId,
        ReadOnlyMemory<byte> body,
        int attempt,
        CancellationToken cancellationToken)
    {
        var message = Deserialize(body);

        // Known only after deserializing, so it is pushed here rather than in the consumer.
        using var orderScope = LogContext.PushProperty("OrderId", message.OrderId);

        if (message.SchemaVersion > MessageContracts.CurrentSchemaVersion)
        {
            // A message from a newer publisher than this build understands. Retrying cannot help -
            // this process will not learn the new schema by waiting - so it is parked immediately
            // for a human to look at, which is usually "finish the rolling deploy".
            throw new PermanentMessageFailureException(
                $"Schema version {message.SchemaVersion} is newer than this build understands " +
                $"({MessageContracts.CurrentSchemaVersion}).");
        }

        InjectConfiguredFault(message, attempt);

        var order = await database.Orders
            .FirstOrDefaultAsync(o => o.Id == message.OrderId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PermanentMessageFailureException(
                $"No order {message.OrderId}. The message describes something that does not exist, " +
                "so no amount of retrying will make it processable.");

        if (order.Status == OrderStatus.Completed)
        {
            // A cheap short-circuit for the common duplicate, which saves rendering a PDF that will
            // be thrown away. It is NOT the idempotency mechanism: two concurrent deliveries would
            // both read Accepted here and both proceed. The unique index below is what actually
            // adjudicates that race.
            WorkerLog.AlreadyCompleted(logger, order.Id);
            return;
        }

        var generatedAt = clock.GetUtcNow();
        var pdf = renderer.Render(message, generatedAt);

        database.Receipts.Add(new Receipt
        {
            OrderId = order.Id,
            GeneratedAt = generatedAt,
            ContentType = ReceiptRenderer.ContentType,
            Content = pdf,
        });

        // The record that this message has been handled goes in the SAME transaction as its effects.
        // Written separately - before, or after - there would be a window in which the work is done
        // and unrecorded, or recorded and not done, and a crash in that window produces exactly the
        // duplicate this is meant to prevent.
        database.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = messageId,
            OrderId = order.Id,
            ProcessedAt = generatedAt,
        });

        order.Status = OrderStatus.Completed;
        order.CompletedAt = generatedAt;

        try
        {
            // One SaveChangesAsync: the receipt, the inbox row, and the status that advertises them
            // all land together or not at all.
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // Another delivery of this same message won the race and has already done the work.
            // This is a success, not a failure: the effect the message asked for has happened
            // exactly once. Acknowledging is correct; retrying would be pointless and parking it
            // would be wrong.
            WorkerLog.DuplicateIgnored(logger, messageId, message.OrderId);
            return;
        }

        WorkerLog.ReceiptGenerated(logger, order.Id, pdf.Length);
    }

    /// <summary>
    /// Whether a save failed because of a unique-constraint violation - Postgres SQLSTATE 23505.
    ///
    /// Matched on the SQLSTATE rather than on the message text, which is localised and version
    /// dependent. Matching on text is how this check quietly stops working after an upgrade.
    /// </summary>
    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Fails on purpose, if configured to. Does nothing at all unless one of the fault options is
    /// set, which is the case everywhere except a demonstration or a test.
    /// </summary>
    private void InjectConfiguredFault(OrderPlaced message, int attempt)
    {
        var permanent = _faults.FailPermanentlyForEmailContaining;
        if (!string.IsNullOrEmpty(permanent) &&
            message.CustomerEmail.Contains(permanent, StringComparison.OrdinalIgnoreCase))
        {
            throw new PermanentMessageFailureException(
                $"Fault injection: orders for '{permanent}' are configured to fail permanently.");
        }

        var transient = _faults.FailTransientlyForEmailContaining;
        if (!string.IsNullOrEmpty(transient) &&
            message.CustomerEmail.Contains(transient, StringComparison.OrdinalIgnoreCase) &&
            (_faults.SucceedAfterAttempts <= 0 || attempt <= _faults.SucceedAfterAttempts))
        {
            // An ordinary exception, NOT PermanentMessageFailureException - so it goes round the
            // backoff ladder exactly as a real transient failure would.
            throw new InvalidOperationException(
                $"Fault injection: attempt {attempt} for '{transient}' is configured to fail transiently.");
        }
    }

    private static OrderPlaced Deserialize(ReadOnlyMemory<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize(body.Span, ContractsSerializerContext.Default.OrderPlaced)
                ?? throw new PermanentMessageFailureException("The message body deserialized to null.");
        }
        catch (JsonException ex)
        {
            // Includes a body that is not JSON at all and one that is missing a required field.
            // Both are permanent: the bytes will not improve on a second reading.
            throw new PermanentMessageFailureException(
                $"The message body is not a valid {nameof(OrderPlaced)}: {ex.Message}", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new PermanentMessageFailureException("The message body is not valid UTF-8.", ex);
        }
    }
}
