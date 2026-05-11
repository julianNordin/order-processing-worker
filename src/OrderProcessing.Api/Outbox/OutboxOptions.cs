using System.ComponentModel.DataAnnotations;

namespace OrderProcessing.Api.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>
    /// How long to wait after finding nothing to publish.
    ///
    /// This is the latency an order sees between being accepted and reaching the broker, so it
    /// wants to be short. It is also a query against the database per interval per instance, so it
    /// cannot be zero. One second is a deliberate middle.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many rows one pass claims.
    ///
    /// The batch is held under a row lock for as long as it takes to publish all of it, so a large
    /// batch means a long-lived transaction and rows another instance cannot touch. A full batch
    /// causes an immediate next pass, so a backlog still drains quickly.
    /// </summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// How long one pass may spend talking to the broker before giving up on it.
    ///
    /// This exists because the connection layer waits patiently for an unreachable broker - which is
    /// right at startup and wrong here. A publish attempt that blocks for two minutes holds this
    /// batch's row locks and a database connection for two minutes, so a broker outage would quietly
    /// turn into database pressure, and the attempt counters would never advance because nothing
    /// ever failed.
    ///
    /// Failing fast instead means the transaction rolls back in seconds, the rows stay unpublished,
    /// the failure is recorded, and the next interval tries again. Waiting is what the poll loop is
    /// for; it does not need to happen inside a transaction as well.
    /// </summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
