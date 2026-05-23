using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace OrderProcessing.Messaging;

/// <summary>
/// Reports whether this process can actually reach the broker right now.
///
/// It opens a channel rather than inspecting a cached connection flag. A connection object can say
/// it is open while the socket underneath has quietly gone - and a consumer that believes it is
/// connected but receives nothing is the exact failure this check exists to catch. Opening a channel
/// is a round trip, so the broker has to answer.
/// </summary>
internal sealed class BrokerHealthCheck(IRabbitMqConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var channel = await connection
                .CreateChannelAsync(publisherConfirms: false, cancellationToken)
                .ConfigureAwait(false);

            return channel.IsOpen
                ? HealthCheckResult.Healthy("Broker reachable.")
                : HealthCheckResult.Unhealthy("Opened a channel to the broker but it is not open.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Cannot reach the broker.", ex);
        }
    }
}
