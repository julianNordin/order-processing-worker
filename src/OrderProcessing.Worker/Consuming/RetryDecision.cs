using OrderProcessing.Messaging;

namespace OrderProcessing.Worker.Consuming;

/// <summary>What to do with a delivery that failed.</summary>
public enum FailureAction
{
    /// <summary>Send it round the backoff ladder and try again later.</summary>
    Retry,

    /// <summary>Park it in the dead-letter queue. No further attempts.</summary>
    Park,
}

/// <summary>
/// The decision, and the reason for it, kept separate from the code that acts on it.
/// </summary>
/// <param name="Action">Retry or park.</param>
/// <param name="Tier">Which wait queue to use. Null when parking.</param>
/// <param name="Attempt">The attempt number this failure represents, starting at 1.</param>
/// <param name="Reason">Why, in words, for the header on a parked message and for the log.</param>
public sealed record RetryDecision(FailureAction Action, RetryTier? Tier, int Attempt, string Reason)
{
    /// <summary>
    /// Decides what happens to a failed delivery.
    ///
    /// The whole design rests on one distinction: <b>will trying again ever help?</b>
    ///
    /// A database that was briefly unreachable, a timeout, a broker hiccup — those are transient,
    /// and the same message will very likely succeed in thirty seconds. Malformed JSON, a schema
    /// version from the future, an order that does not exist — those are permanent, and retrying
    /// them three times over two and a half minutes achieves nothing except delaying every message
    /// queued behind them and filling the logs with identical failures.
    ///
    /// Getting this wrong in the safe-looking direction (retry everything) is the common mistake. It
    /// turns one bad message into a slow, repeating, self-inflicted outage.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="previousAttempts">
    /// The value of the <c>x-attempt</c> header on the delivery, or zero on first delivery.
    /// </param>
    public static RetryDecision For(Exception exception, int previousAttempts)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var attempt = previousAttempts + 1;

        if (exception is PermanentMessageFailureException)
        {
            return new RetryDecision(
                FailureAction.Park, Tier: null, attempt,
                $"Permanent failure, not retried: {exception.Message}");
        }

        var tier = MessagingTopology.TierForAttempt(attempt);

        if (tier is null)
        {
            // Says "attempt", matching the x-attempt header on the parked message. Reporting the
            // number of RETRIES here instead reads as a contradiction next to that header - the
            // message was delivered four times and retried three, and an operator comparing the two
            // numbers should not have to work that out.
            return new RetryDecision(
                FailureAction.Park, Tier: null, attempt,
                $"Giving up after {attempt} attempts ({MessagingTopology.MaxAttempts} retries). " +
                $"Last failure: {exception.GetType().Name}: {exception.Message}");
        }

        return new RetryDecision(
            FailureAction.Retry, tier, attempt,
            $"{exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>
    /// Reads the attempt count a previous delivery left behind.
    ///
    /// RabbitMQ header values arrive as byte arrays rather than as the types they were published as,
    /// so a header written as an int comes back as something that has to be converted. Anything
    /// unreadable is treated as zero: a message whose header cannot be parsed gets a full set of
    /// retries rather than being parked on a technicality.
    /// </summary>
    public static int ReadAttempt(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue(MessagingTopology.AttemptHeader, out var raw) || raw is null)
        {
            return 0;
        }

        return raw switch
        {
            int value => value,
            long value => (int)value,
            byte[] bytes when int.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => 0,
        };
    }
}
