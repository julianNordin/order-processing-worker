namespace OrderProcessing.Worker.Consuming;

internal static partial class WorkerLog
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Information,
        Message = "Consuming {Queue} with prefetch {PrefetchCount} (consumer tag {ConsumerTag})")]
    public static partial void ConsumerStarted(ILogger logger, string queue, ushort prefetchCount, string consumerTag);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning,
        Message = "Could not start consuming; retrying. The topology may not be declared yet")]
    public static partial void ConsumerStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information,
        Message = "Generated receipt for order {OrderId} ({Bytes} bytes)")]
    public static partial void ReceiptGenerated(ILogger logger, Guid orderId, int bytes);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Information,
        Message = "Order {OrderId} is already completed; this delivery is a duplicate and is being acknowledged")]
    public static partial void AlreadyCompleted(ILogger logger, Guid orderId);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Warning,
        Message = "Message {MessageId} failed on attempt {Attempt}; retrying after {Delay}")]
    public static partial void Retrying(ILogger logger, Exception exception, string messageId, int attempt, TimeSpan delay);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Error,
        Message = "Message {MessageId} parked after {Attempt} attempt(s): {Reason}")]
    public static partial void Parking(ILogger logger, Exception exception, string messageId, int attempt, string reason);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Error,
        Message = "Could not route the failure for message {MessageId}; it stays unacknowledged and will be redelivered")]
    public static partial void FailureRoutingFailed(ILogger logger, Exception exception, string messageId);
}
