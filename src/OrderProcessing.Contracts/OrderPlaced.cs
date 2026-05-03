namespace OrderProcessing.Contracts;

/// <summary>
/// Published when a customer's order has been accepted and needs a receipt generated.
///
/// This is the payload only. The identifying metadata a message needs — its id, the correlation id
/// that ties it back to the HTTP request that caused it, and when it happened — travels in the AMQP
/// basic properties rather than in here. Those fields exist in the protocol for exactly this
/// purpose, and keeping them there means the management UI and any broker tooling can read them
/// without deserializing a body they know nothing about.
/// </summary>
public sealed record OrderPlaced
{
    /// <summary>
    /// The version of this contract that the publisher was compiled against.
    ///
    /// It is carried in the payload rather than inferred, so a consumer can reject a message from
    /// the future explicitly instead of silently deserializing it into a shape with missing fields.
    /// See <see cref="MessageContracts"/> for the rule that governs changes to this number.
    /// </summary>
    public required int SchemaVersion { get; init; }

    public required Guid OrderId { get; init; }

    public required string CustomerEmail { get; init; }

    public required IReadOnlyList<OrderLine> Lines { get; init; }

    /// <summary>
    /// The order total as the API calculated it.
    ///
    /// Deliberately transmitted rather than recalculated by the worker. If the two ever disagree,
    /// that is a defect worth seeing, and it cannot be seen if only one side ever does the sum.
    /// </summary>
    public required decimal Total { get; init; }
}
