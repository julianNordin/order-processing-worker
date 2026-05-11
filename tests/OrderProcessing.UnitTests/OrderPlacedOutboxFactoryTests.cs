using System.Text.Json;
using OrderProcessing.Api.Outbox;
using OrderProcessing.Contracts;
using OrderProcessing.Persistence.Entities;

using EntityLine = OrderProcessing.Persistence.Entities.OrderLine;

namespace OrderProcessing.UnitTests;

/// <summary>
/// The mapping from a stored order to the message that describes it.
///
/// Worth testing precisely because getting it wrong is quiet: a dropped line or a total that
/// disagrees with the order produces a receipt that is merely incorrect. Nothing errors, nothing
/// retries, and the customer is the one who notices.
/// </summary>
public class OrderPlacedOutboxFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 11, 10, 30, 0, TimeSpan.Zero);

    private static Order AnOrder()
    {
        var order = new Order
        {
            Id = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"),
            CustomerEmail = "buyer@example.com",
            Total = 46.97m,
            Status = OrderStatus.Accepted,
            PlacedAt = Now,
        };

        order.Lines.Add(new EntityLine { Sku = "SKU-1", Description = "Blue widget", Quantity = 3, UnitPrice = 13.99m });
        order.Lines.Add(new EntityLine { Sku = "SKU-2", Description = "Red widget", Quantity = 1, UnitPrice = 5.00m });
        return order;
    }

    private static OrderPlaced PayloadOf(OutboxMessage message) =>
        JsonSerializer.Deserialize(message.Payload, ContractsSerializerContext.Default.OrderPlaced)!;

    [Fact]
    public void Routes_to_the_exchange_and_key_the_consumer_is_bound_to()
    {
        // These two strings are the entire routing decision. A typo in either is not an error - the
        // publish succeeds and the message is discarded - so they are asserted against the shared
        // constants rather than against literals typed twice.
        var message = OrderPlacedOutboxFactory.For(AnOrder(), "corr-1", Now);

        Assert.Equal(MessageContracts.Exchange, message.Exchange);
        Assert.Equal(MessageContracts.OrderPlacedRoutingKey, message.RoutingKey);
        Assert.Equal(MessageContracts.OrderPlacedMessageType, message.MessageType);
    }

    [Fact]
    public void Carries_the_order_faithfully_including_every_line()
    {
        var order = AnOrder();

        var payload = PayloadOf(OrderPlacedOutboxFactory.For(order, "corr-1", Now));

        Assert.Equal(order.Id, payload.OrderId);
        Assert.Equal(order.CustomerEmail, payload.CustomerEmail);
        Assert.Equal(order.Total, payload.Total);
        Assert.Equal(2, payload.Lines.Count);
        Assert.Equal("SKU-1", payload.Lines[0].Sku);
        Assert.Equal(3, payload.Lines[0].Quantity);
        Assert.Equal(13.99m, payload.Lines[0].UnitPrice);
        Assert.Equal("SKU-2", payload.Lines[1].Sku);
    }

    [Fact]
    public void Stamps_the_schema_version_so_a_consumer_can_refuse_a_message_from_the_future()
    {
        var payload = PayloadOf(OrderPlacedOutboxFactory.For(AnOrder(), "corr-1", Now));

        Assert.Equal(MessageContracts.CurrentSchemaVersion, payload.SchemaVersion);
    }

    [Fact]
    public void Starts_unpublished_with_no_attempts_and_no_error()
    {
        // published_at IS NULL is the publisher's entire query. A row created as anything else would
        // never be sent, and nothing would report it.
        var message = OrderPlacedOutboxFactory.For(AnOrder(), "corr-1", Now);

        Assert.Null(message.PublishedAt);
        Assert.Equal(0, message.Attempts);
        Assert.Null(message.LastError);
        Assert.Equal(Now, message.OccurredAt);
        Assert.Equal("corr-1", message.CorrelationId);
    }

    [Fact]
    public void Gives_every_message_its_own_id()
    {
        // The message id is the consumer's deduplication key. Two orders sharing one would make the
        // second look like a duplicate of the first and be silently discarded.
        var first = OrderPlacedOutboxFactory.For(AnOrder(), "corr-1", Now);
        var second = OrderPlacedOutboxFactory.For(AnOrder(), "corr-1", Now);

        Assert.NotEqual(first.MessageId, second.MessageId);
        Assert.NotEqual(Guid.Empty, first.MessageId);
    }

    [Fact]
    public void Writes_a_payload_postgres_will_accept_as_jsonb()
    {
        // The column is jsonb, so Postgres parses this on the way in. A payload that is not valid
        // JSON fails the insert - which would take the order down with it, since they share a
        // transaction.
        var message = OrderPlacedOutboxFactory.For(AnOrder(), "corr-1", Now);

        var parsed = JsonDocument.Parse(message.Payload);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        Assert.True(parsed.RootElement.TryGetProperty("orderId", out _));
    }
}
