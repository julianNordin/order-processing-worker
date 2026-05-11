using System.Text.Json;
using OrderProcessing.Contracts;
using OrderProcessing.Persistence.Entities;

using ContractLine = OrderProcessing.Contracts.OrderLine;

namespace OrderProcessing.Api.Outbox;

/// <summary>
/// Turns an accepted order into the outbox row that will carry it to the broker.
///
/// Separate from the endpoint so it can be tested without a host, a database or a broker - and
/// because getting this mapping wrong is quiet. A dropped line or a total that disagrees with the
/// stored order produces a receipt that is merely incorrect, not an error anyone would notice.
/// </summary>
public static class OrderPlacedOutboxFactory
{
    public static OutboxMessage For(Order order, string correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(order);

        // Serialized here, at the moment the order is accepted, rather than rebuilt by the publisher
        // at send time. A message describes something that HAPPENED; if the publisher re-read the
        // order later, an edit in between would silently rewrite history.
        var contract = new OrderPlaced
        {
            SchemaVersion = MessageContracts.CurrentSchemaVersion,
            OrderId = order.Id,
            CustomerEmail = order.CustomerEmail,
            Total = order.Total,
            Lines =
            [
                .. order.Lines.Select(l => new ContractLine
                {
                    Sku = l.Sku,
                    Description = l.Description,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                }),
            ],
        };

        return new OutboxMessage
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = correlationId,
            MessageType = MessageContracts.OrderPlacedMessageType,
            Exchange = MessageContracts.Exchange,
            RoutingKey = MessageContracts.OrderPlacedRoutingKey,
            Payload = JsonSerializer.Serialize(contract, ContractsSerializerContext.Default.OrderPlaced),
            OccurredAt = now,
        };
    }
}
