using OrderProcessing.Persistence.Entities;

namespace OrderProcessing.Api.Orders;

/// <summary>What a caller gets back when asking about an order.</summary>
public sealed record OrderResponse(
    Guid OrderId,
    string Status,
    string CustomerEmail,
    decimal Total,
    DateTimeOffset PlacedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    IReadOnlyList<OrderLineResponse> Lines)
{
    public static OrderResponse From(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new OrderResponse(
            order.Id,
            order.Status.ToString(),
            order.CustomerEmail,
            order.Total,
            order.PlacedAt,
            order.CompletedAt,
            order.FailureReason,
            [.. order.Lines.Select(l => new OrderLineResponse(l.Sku, l.Description, l.Quantity, l.UnitPrice))]);
    }
}

public sealed record OrderLineResponse(string Sku, string Description, int Quantity, decimal UnitPrice);

/// <summary>
/// The body of the 202. Deliberately small: the order has been accepted and nothing else has
/// happened yet, so there is nothing else true to say about it.
/// </summary>
public sealed record OrderAcceptedResponse(Guid OrderId, string Status);
