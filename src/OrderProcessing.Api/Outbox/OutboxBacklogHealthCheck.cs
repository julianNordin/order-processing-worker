using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OrderProcessing.Persistence;

namespace OrderProcessing.Api.Outbox;

/// <summary>
/// Reports the outbox backlog: how many messages have been accepted but not yet reached the broker.
///
/// This is the one health signal that is genuinely specific to this design. Everything else can be
/// healthy - the database answers, the broker answers, the API returns 202s - while the outbox
/// quietly stops draining, and the only visible symptom is that receipts take longer and longer to
/// appear. A count of unpublished rows makes that visible before a customer reports it.
///
/// A backlog is <b>Degraded</b>, not Unhealthy, and the distinction matters: degraded keeps the
/// instance in rotation. Taking it out would remove the very capacity that has to clear the backlog.
/// </summary>
internal sealed class OutboxBacklogHealthCheck(
    OrderProcessingDbContext database,
    IOptions<OutboxOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var backlog = await database.OutboxMessages
                .CountAsync(m => m.PublishedAt == null, cancellationToken)
                .ConfigureAwait(false);

            var threshold = options.Value.BacklogWarningThreshold;
            var data = new Dictionary<string, object> { ["backlog"] = backlog };

            return backlog > threshold
                ? HealthCheckResult.Degraded($"{backlog} messages are waiting to be published (threshold {threshold}).", data: data)
                : HealthCheckResult.Healthy($"{backlog} messages waiting.", data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Could not read the outbox backlog.", ex);
        }
    }
}
