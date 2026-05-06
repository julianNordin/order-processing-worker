using System.ComponentModel.DataAnnotations;

namespace OrderProcessing.Messaging;

/// <summary>
/// How to reach the broker. Bound from configuration under "RabbitMq" and validated at startup, so
/// a missing password fails the moment the process starts rather than on the first publish.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 5672;

    [Required(AllowEmptyStrings = false)]
    public string UserName { get; set; } = "";

    [Required(AllowEmptyStrings = false)]
    public string Password { get; set; } = "";

    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// How many unacknowledged messages the broker will hand a consumer at once.
    ///
    /// Zero means unlimited in AMQP, which would let the worker pull an entire queue into memory,
    /// so the range starts at one. The value is a throughput/fairness trade-off: too low and the
    /// consumer idles between round trips, too high and one slow consumer hoards work that an idle
    /// one could be doing. Tuned, with numbers, in Phase 13.
    /// </summary>
    [Range(1, 1000)]
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>
    /// How long to keep retrying the initial connection before giving up.
    ///
    /// Needed because Compose starts the broker and the services together: without this the worker
    /// crashes on startup roughly every time, and the restart loop hides it.
    /// </summary>
    public TimeSpan ConnectionRetryTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Name this connection reports to the broker. It shows in the management UI's connection list,
    /// which is the difference between diagnosing a stuck consumer in seconds and guessing.
    /// </summary>
    public string ClientProvidedName { get; set; } = "orderprocessing";
}
