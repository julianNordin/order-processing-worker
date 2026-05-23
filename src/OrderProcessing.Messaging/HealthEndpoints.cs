using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace OrderProcessing.Messaging;

/// <summary>
/// The two health endpoints both services expose, and the distinction between them.
///
/// <b>Liveness</b> answers "is this process wedged, should it be killed and restarted?" It checks
/// nothing external, deliberately. A liveness probe that fails when the database is down causes the
/// orchestrator to kill every replica of a perfectly healthy service during a database outage,
/// turning a recoverable dependency failure into a total one. This is a well-known way to make an
/// incident worse, and the fix is for liveness to check only the process itself.
///
/// <b>Readiness</b> answers "should traffic be sent here, or should this instance be waited for?"
/// That one does check dependencies, because an instance that cannot reach its database has nothing
/// useful to do with a request.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Tag marking the checks that belong to readiness rather than liveness.</summary>
    public const string ReadinessTag = "ready";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Liveness: no checks at all. Answering at all is the whole signal - it proves the process
        // is running, the host is accepting connections, and the thread pool is not exhausted.
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadinessTag),
            ResponseWriter = WriteResponseAsync,

            // Degraded stays a 200. It means "working, but something is worth looking at" - an
            // outbox backlog, for instance - and taking the instance out of rotation for it would
            // remove the very capacity that is needed to clear the backlog.
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        });

        return endpoints;
    }

    /// <summary>
    /// Writes each check's own result, not merely the aggregate.
    ///
    /// The default response body is the single word "Healthy". During an incident the useful
    /// question is <i>which</i> dependency is down, and a one-word answer means opening the logs to
    /// find out something the probe already knew.
    /// </summary>
    private static async Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    durationMs = entry.Value.Duration.TotalMilliseconds,
                    error = entry.Value.Exception?.Message,
                },
                StringComparer.Ordinal),
        };

        await context.Response
            .WriteAsync(JsonSerializer.Serialize(payload, HealthJsonOptions))
            .ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions HealthJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Registers the broker check under the readiness tag.</summary>
    /// <param name="builder">The health checks builder.</param>
    public static IHealthChecksBuilder AddBrokerCheck(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCheck<BrokerHealthCheck>(
            "broker",
            failureStatus: HealthStatus.Unhealthy,
            tags: [ReadinessTag]);
    }
}
