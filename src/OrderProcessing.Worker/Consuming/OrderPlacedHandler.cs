using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderProcessing.Contracts;
using OrderProcessing.Persistence;
using OrderProcessing.Persistence.Entities;
using OrderProcessing.Worker.Receipts;

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
    TimeProvider clock,
    ILogger<OrderPlacedHandler> logger)
{
    public async Task HandleAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var message = Deserialize(body);

        if (message.SchemaVersion > MessageContracts.CurrentSchemaVersion)
        {
            // A message from a newer publisher than this build understands. Retrying cannot help -
            // this process will not learn the new schema by waiting - so it is parked immediately
            // for a human to look at, which is usually "finish the rolling deploy".
            throw new PermanentMessageFailureException(
                $"Schema version {message.SchemaVersion} is newer than this build understands " +
                $"({MessageContracts.CurrentSchemaVersion}).");
        }

        var order = await database.Orders
            .FirstOrDefaultAsync(o => o.Id == message.OrderId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PermanentMessageFailureException(
                $"No order {message.OrderId}. The message describes something that does not exist, " +
                "so no amount of retrying will make it processable.");

        if (order.Status == OrderStatus.Completed)
        {
            // Cheap short-circuit for the common duplicate. It is NOT the idempotency mechanism -
            // that arrives in Phase 12 and is enforced by the database, because this check races
            // with a concurrent delivery of the same message.
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

        order.Status = OrderStatus.Completed;
        order.CompletedAt = generatedAt;

        // One SaveChangesAsync, so the receipt and the status that advertises it land together.
        // A receipt with the order still marked Accepted would be invisible; an order marked
        // Completed with no receipt would 404 on download.
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        WorkerLog.ReceiptGenerated(logger, order.Id, pdf.Length);
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
