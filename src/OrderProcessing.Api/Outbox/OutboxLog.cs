namespace OrderProcessing.Api.Outbox;

internal static partial class OutboxLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Debug,
        Message = "Published outbox message {MessageId} with routing key {RoutingKey}")]
    public static partial void Published(ILogger logger, Guid messageId, string routingKey);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
        Message = "Failed to publish outbox message {MessageId} (attempt {Attempts}); it stays unpublished and will be retried")]
    public static partial void PublishFailed(ILogger logger, Exception exception, Guid messageId, int attempts);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error,
        Message = "Outbox batch failed; the publisher will try again on the next interval")]
    public static partial void BatchFailed(ILogger logger, Exception exception);
}
