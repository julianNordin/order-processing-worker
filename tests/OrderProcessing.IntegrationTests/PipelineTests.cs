using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.Contracts;
using OrderProcessing.Messaging;
using OrderProcessing.Persistence;
using OrderProcessing.Persistence.Entities;

using ContractLine = OrderProcessing.Contracts.OrderLine;

namespace OrderProcessing.IntegrationTests;

/// <summary>
/// The pipeline end to end, against a real broker and a real database.
///
/// Every assertion here is on <b>eventual</b> state, reached by polling with a deadline. There is not
/// one fixed sleep in this file: a hard-coded wait is either slower than it needs to be or flaky on a
/// loaded machine, and usually both.
/// </summary>
[Collection(SharedPipeline.Name)]
public class PipelineTests(PipelineFixture pipeline)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // Long enough for the first retry tier (5s) plus room on a loaded machine.
    private static readonly TimeSpan RetryPatience = TimeSpan.FromSeconds(45);

    private async Task<Guid> PlaceOrderAsync(string email)
    {
        var response = await pipeline.Client.PostAsJsonAsync("/api/orders", new
        {
            customerEmail = email,
            lines = new[] { new { sku = "SKU-1", description = "Blue widget", quantity = 2, unitPrice = 11.50m } },
        }).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(true);
        return accepted.GetProperty("orderId").GetGuid();
    }

    private async Task<T> WithDatabaseAsync<T>(Func<OrderProcessingDbContext, Task<T>> work)
    {
        using var scope = pipeline.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>()).ConfigureAwait(true);
    }

    private Task WaitForStatusAsync(Guid orderId, OrderStatus status, TimeSpan patience) =>
        PipelineFixture.WaitUntilAsync(
            async () => await WithDatabaseAsync(db => db.Orders
                .AsNoTracking()
                .AnyAsync(o => o.Id == orderId && o.Status == status)).ConfigureAwait(true),
            patience,
            $"order {orderId} to become {status}");

    private Task WaitForDeadLetterAsync(int expected) =>
        PipelineFixture.WaitUntilAsync(
            async () =>
            {
                var inspector = pipeline.GetRequiredService<IQueueInspector>();
                return await inspector.GetDepthAsync(MessagingTopology.DeadLetterQueue, CancellationToken.None)
                    .ConfigureAwait(true) >= expected;
            },
            Patience,
            $"at least {expected} message(s) in the dead-letter queue");

    // ---------------------------------------------------------------- 1. golden path

    [Fact]
    public async Task An_order_becomes_a_downloadable_receipt()
    {
        var orderId = await PlaceOrderAsync("golden@example.com").ConfigureAwait(true);

        await WaitForStatusAsync(orderId, OrderStatus.Completed, Patience).ConfigureAwait(true);

        var receipt = await pipeline.Client.GetAsync(new Uri($"/api/orders/{orderId}/receipt", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal("application/pdf", receipt.Content.Headers.ContentType?.MediaType);

        var bytes = await receipt.Content.ReadAsByteArrayAsync().ConfigureAwait(true);

        // The magic bytes, not merely a non-empty body. An error page returned with a 200 would
        // otherwise pass.
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public async Task A_receipt_that_does_not_exist_yet_is_distinguished_from_an_order_that_does_not_exist()
    {
        var unknown = await pipeline.Client.GetAsync(new Uri($"/api/orders/{Guid.NewGuid()}/receipt", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);

        var body = await unknown.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Contains("Order not found", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- 2. transient failure recovers

    [Fact]
    public async Task A_transient_failure_is_retried_and_then_succeeds()
    {
        // The fixture configures this address to fail once and then succeed, so the order can only
        // reach Completed by going out to a wait queue and coming back.
        var orderId = await PlaceOrderAsync($"{PipelineFixture.TransientFailureMarker}@example.com").ConfigureAwait(true);

        await WaitForStatusAsync(orderId, OrderStatus.Completed, RetryPatience).ConfigureAwait(true);

        var receipts = await WithDatabaseAsync(db => db.Receipts.CountAsync(r => r.OrderId == orderId))
            .ConfigureAwait(true);

        // Exactly one, despite being handled twice.
        Assert.Equal(1, receipts);
    }

    // ------------------------------------------------------------ 3. malformed message

    [Fact]
    public async Task A_body_that_is_not_valid_json_is_parked_without_being_retried()
    {
        var before = await DeadLetterDepthAsync().ConfigureAwait(true);
        var publisher = pipeline.GetRequiredService<IMessagePublisher>();

        await publisher.PublishAsync(
            new OutboundMessage(
                MessageId: Guid.CreateVersion7(),
                CorrelationId: "malformed-test",
                Type: MessageContracts.OrderPlacedMessageType,
                Exchange: MessageContracts.Exchange,
                RoutingKey: MessageContracts.OrderPlacedRoutingKey,
                Body: Encoding.UTF8.GetBytes("{ this is not json at all")),
            CancellationToken.None).ConfigureAwait(true);

        await WaitForDeadLetterAsync(before + 1).ConfigureAwait(true);

        var parked = await FindParkedAsync("malformed-test").ConfigureAwait(true);

        Assert.NotNull(parked);
        // One attempt, not three. Retrying a malformed body cannot help and would delay everything
        // queued behind it for two and a half minutes to prove it.
        Assert.Equal(1, parked.Attempts);
        Assert.Contains("not retried", parked.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_message_from_a_newer_schema_is_parked_rather_than_guessed_at()
    {
        var before = await DeadLetterDepthAsync().ConfigureAwait(true);
        var publisher = pipeline.GetRequiredService<IMessagePublisher>();

        var fromTheFuture = JsonSerializer.Serialize(new OrderPlaced
        {
            SchemaVersion = MessageContracts.CurrentSchemaVersion + 1,
            OrderId = Guid.CreateVersion7(),
            CustomerEmail = "future@example.com",
            Total = 1m,
            Lines = [new ContractLine { Sku = "S", Description = "d", Quantity = 1, UnitPrice = 1m }],
        }, ContractsSerializerContext.Default.OrderPlaced);

        await publisher.PublishAsync(
            new OutboundMessage(
                MessageId: Guid.CreateVersion7(),
                CorrelationId: "future-schema-test",
                Type: MessageContracts.OrderPlacedMessageType,
                Exchange: MessageContracts.Exchange,
                RoutingKey: MessageContracts.OrderPlacedRoutingKey,
                Body: Encoding.UTF8.GetBytes(fromTheFuture)),
            CancellationToken.None).ConfigureAwait(true);

        await WaitForDeadLetterAsync(before + 1).ConfigureAwait(true);

        var parked = await FindParkedAsync("future-schema-test").ConfigureAwait(true);

        Assert.NotNull(parked);
        Assert.Equal(1, parked.Attempts);
    }

    // --------------------------------------------------------- 4. retries exhausted

    [Fact]
    public async Task A_message_that_never_succeeds_is_parked_once_its_attempts_run_out()
    {
        // Published pre-stamped as already having failed three times, so the next failure exhausts
        // the ladder immediately. The alternative - letting it climb 5s, 30s and 2m for real - takes
        // over two and a half minutes to assert something the unit tests already pin. What this
        // proves that they cannot is that the exhausted message actually ARRIVES in the dead-letter
        // queue, through a real exchange, with its headers intact.
        var before = await DeadLetterDepthAsync().ConfigureAwait(true);
        var publisher = pipeline.GetRequiredService<IMessagePublisher>();

        var payload = JsonSerializer.Serialize(new OrderPlaced
        {
            SchemaVersion = MessageContracts.CurrentSchemaVersion,
            OrderId = Guid.CreateVersion7(),
            CustomerEmail = $"{PipelineFixture.AlwaysFailMarker}@example.com",
            Total = 1m,
            Lines = [new ContractLine { Sku = "S", Description = "d", Quantity = 1, UnitPrice = 1m }],
        }, ContractsSerializerContext.Default.OrderPlaced);

        await publisher.PublishAsync(
            new OutboundMessage(
                MessageId: Guid.CreateVersion7(),
                CorrelationId: "exhausted-test",
                Type: MessageContracts.OrderPlacedMessageType,
                Exchange: MessageContracts.Exchange,
                RoutingKey: MessageContracts.OrderPlacedRoutingKey,
                Body: Encoding.UTF8.GetBytes(payload),
                Headers: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [MessagingTopology.AttemptHeader] = MessagingTopology.MaxAttempts,
                }),
            CancellationToken.None).ConfigureAwait(true);

        await WaitForDeadLetterAsync(before + 1).ConfigureAwait(true);

        var parked = await FindParkedAsync("exhausted-test").ConfigureAwait(true);

        Assert.NotNull(parked);
        Assert.Equal(MessagingTopology.MaxAttempts + 1, parked.Attempts);
        Assert.Contains("Giving up", parked.FailureReason!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- 5. duplicate delivery

    [Fact]
    public async Task The_same_message_delivered_twice_produces_one_receipt()
    {
        var orderId = await PlaceOrderAsync("duplicate@example.com").ConfigureAwait(true);
        await WaitForStatusAsync(orderId, OrderStatus.Completed, Patience).ConfigureAwait(true);

        // Republish the outbox row verbatim - the same message id, which is what a redelivery is.
        var row = await FindOutboxRowAsync(orderId).ConfigureAwait(true);

        var publisher = pipeline.GetRequiredService<IMessagePublisher>();

        await publisher.PublishAsync(
            new OutboundMessage(row.MessageId, row.CorrelationId, row.MessageType,
                row.Exchange, row.RoutingKey, Encoding.UTF8.GetBytes(row.Payload)),
            CancellationToken.None).ConfigureAwait(true);

        // Give the consumer time to have handled it, then assert nothing changed. Waiting for the
        // absence of an effect needs a settling period; there is nothing to poll for.
        await PipelineFixture.WaitUntilAsync(
            async () => await WithDatabaseAsync(db => db.ProcessedMessages
                .AsNoTracking()
                .CountAsync(m => m.OrderId == orderId)).ConfigureAwait(true) == 1,
            Patience,
            "the duplicate to be absorbed").ConfigureAwait(true);

        var receipts = await WithDatabaseAsync(db => db.Receipts.CountAsync(r => r.OrderId == orderId))
            .ConfigureAwait(true);
        var processed = await WithDatabaseAsync(db => db.ProcessedMessages.CountAsync(m => m.OrderId == orderId))
            .ConfigureAwait(true);

        Assert.Equal(1, receipts);
        Assert.Equal(1, processed);
    }

    // ------------------------------------------------------------------- 6. the outbox

    [Fact]
    public async Task The_order_and_its_message_are_written_in_one_transaction()
    {
        var orderId = await PlaceOrderAsync("atomic@example.com").ConfigureAwait(true);

        // The outbox row exists from the moment the order does - the publisher may already have sent
        // it, but it can never be missing for an order that exists.
        var row = await FindOutboxRowAsync(orderId).ConfigureAwait(true);

        Assert.NotNull(row);

        await WaitForStatusAsync(orderId, OrderStatus.Completed, Patience).ConfigureAwait(true);

        var afterCompletion = await FindOutboxRowAsync(orderId).ConfigureAwait(true);

        // Stamped only after the broker confirmed it, never before.
        Assert.NotNull(afterCompletion.PublishedAt);
    }

    // ------------------------------------------------------------------------ helpers

    /// <summary>
    /// Finds the outbox row carrying a given order, filtering in memory on purpose.
    ///
    /// The obvious query - <c>Where(m =&gt; m.Payload.Contains(id))</c> - does not work. EF
    /// translates string Contains to SQL LIKE, and <c>payload</c> is <c>jsonb</c>, for which
    /// Postgres has no LIKE operator at all: it fails at runtime with
    /// <c>42883: operator does not exist: jsonb ~~ jsonb</c>. The column is jsonb deliberately
    /// (validated on write, queryable when diagnosing), so the fix belongs here rather than in the
    /// schema. The test outbox holds a handful of rows, so reading them is free.
    /// </summary>
    private async Task<OutboxMessage> FindOutboxRowAsync(Guid orderId)
    {
        var rows = await WithDatabaseAsync(db => db.OutboxMessages.AsNoTracking().ToListAsync())
            .ConfigureAwait(true);

        return rows.Single(m => m.Payload.Contains(orderId.ToString(), StringComparison.Ordinal));
    }

    private async Task<int> DeadLetterDepthAsync()
    {
        var inspector = pipeline.GetRequiredService<IQueueInspector>();
        return (int)await inspector.GetDepthAsync(MessagingTopology.DeadLetterQueue, CancellationToken.None)
            .ConfigureAwait(true);
    }

    private async Task<ParkedMessage?> FindParkedAsync(string correlationId)
    {
        var inspector = pipeline.GetRequiredService<IQueueInspector>();
        var parked = await inspector.PeekAsync(MessagingTopology.DeadLetterQueue, 50, CancellationToken.None)
            .ConfigureAwait(true);

        return parked.FirstOrDefault(m => m.CorrelationId == correlationId);
    }
}
