using Microsoft.Extensions.Logging;

namespace OrderProcessing.Messaging;

/// <summary>
/// Every log message this library emits, declared once.
///
/// Source-generated rather than written as logger.LogInformation(...) calls. The generator produces
/// a strongly-typed method that checks IsEnabled before touching its arguments, so a message that is
/// filtered out costs nothing - no boxing of the parameters, no string formatting, no allocation.
/// That is worth having on a consume loop that runs once per message; CA1848 exists to enforce it.
///
/// The second benefit matters more for this project: an event id and a message template declared in
/// one place is what makes the logs queryable rather than merely readable, which is the whole point
/// of the structured logging that arrives in Phase 09.
/// </summary>
internal static partial class MessagingLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Connected to RabbitMQ at {Host}:{Port} on attempt {Attempt}")]
    public static partial void Connected(ILogger logger, string host, int port, int attempt);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "RabbitMQ at {Host}:{Port} not reachable (attempt {Attempt}): {Reason}. Retrying in {Delay}")]
    public static partial void ConnectionAttemptFailed(ILogger logger, string host, int port, int attempt, string reason, TimeSpan delay);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Giving up connecting to RabbitMQ at {Host}:{Port} after {Attempt} attempts")]
    public static partial void ConnectionGaveUp(ILogger logger, Exception exception, string host, int port, int attempt);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information,
        Message = "Topology declared: {ExchangeCount} exchanges, {QueueCount} queues")]
    public static partial void TopologyDeclared(ILogger logger, int exchangeCount, int queueCount);
}
