using Microsoft.AspNetCore.Http.HttpResults;
using OrderProcessing.Messaging;

namespace OrderProcessing.Api.Admin;

/// <summary>
/// Operational endpoints. A dead-letter queue nobody can see is a dead-letter queue nobody empties.
/// </summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/admin").WithTags("Admin");

        group.MapGet("/dlq", GetDeadLetterSummaryAsync)
            .WithName("GetDeadLetterSummary")
            .WithSummary("Reports how many messages are parked, and why.");

        group.MapGet("/queues", GetQueueDepthsAsync)
            .WithName("GetQueueDepths")
            .WithSummary("Reports the depth of every queue in the topology.");

        return group;
    }

    private static async Task<Ok<DeadLetterSummary>> GetDeadLetterSummaryAsync(
        IQueueInspector inspector,
        CancellationToken cancellationToken,
        int limit = 20)
    {
        var depth = await inspector.GetDepthAsync(MessagingTopology.DeadLetterQueue, cancellationToken)
            .ConfigureAwait(false);

        // Reading puts everything back - see IQueueInspector.PeekAsync. This is a window on the
        // queue, not a drain, and looking at a parked message must never be the reason it stops
        // being parked.
        var messages = await inspector.PeekAsync(MessagingTopology.DeadLetterQueue, Math.Clamp(limit, 1, 100), cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new DeadLetterSummary(depth, messages));
    }

    private static async Task<Ok<IReadOnlyDictionary<string, uint>>> GetQueueDepthsAsync(
        IQueueInspector inspector,
        CancellationToken cancellationToken)
    {
        var queues = new[] { MessagingTopology.OrdersPlacedQueue, MessagingTopology.DeadLetterQueue }
            .Concat(MessagingTopology.RetryTiers.Select(t => t.Queue));

        var depths = new Dictionary<string, uint>(StringComparer.Ordinal);

        foreach (var queue in queues)
        {
            depths[queue] = await inspector.GetDepthAsync(queue, cancellationToken).ConfigureAwait(false);
        }

        return TypedResults.Ok<IReadOnlyDictionary<string, uint>>(depths);
    }
}

/// <param name="Depth">Total messages parked.</param>
/// <param name="Messages">The first few, with the reason each was parked.</param>
public sealed record DeadLetterSummary(uint Depth, IReadOnlyList<ParkedMessage> Messages);
