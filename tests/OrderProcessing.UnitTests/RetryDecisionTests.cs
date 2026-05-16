using System.Text;
using OrderProcessing.Messaging;
using OrderProcessing.Worker.Consuming;

namespace OrderProcessing.UnitTests;

/// <summary>
/// The one decision that separates a system which recovers from one that makes its own outages
/// worse: given a failure, will trying again ever help?
///
/// Retrying everything is the mistake that looks safe. A malformed message retried three times over
/// two and a half minutes is still malformed at the end of it, and everything queued behind it has
/// waited.
/// </summary>
public class RetryDecisionTests
{
    private static readonly Exception Transient = new InvalidOperationException("the database blinked");
    private static readonly Exception Permanent = new PermanentMessageFailureException("not valid JSON");

    [Theory]
    [InlineData(0, 1, 5)]      // first failure  -> the 5 second tier
    [InlineData(1, 2, 30)]     // second failure -> 30 seconds
    [InlineData(2, 3, 120)]    // third failure  -> two minutes
    public void A_transient_failure_climbs_the_ladder(int previousAttempts, int expectedAttempt, int expectedDelaySeconds)
    {
        var decision = RetryDecision.For(Transient, previousAttempts);

        Assert.Equal(FailureAction.Retry, decision.Action);
        Assert.Equal(expectedAttempt, decision.Attempt);
        Assert.NotNull(decision.Tier);
        Assert.Equal(TimeSpan.FromSeconds(expectedDelaySeconds), decision.Tier.Delay);
    }

    [Fact]
    public void The_backoff_actually_increases()
    {
        // Guards the ordering of the tiers, not just their presence. A ladder whose rungs are in the
        // wrong order still "retries three times" and is useless: the point of backing off is to give
        // a struggling dependency progressively more room.
        var delays = MessagingTopology.RetryTiers.Select(t => t.Delay).ToArray();

        Assert.Equal(delays.OrderBy(d => d), delays);
        Assert.Equal(delays.Distinct(), delays);
    }

    [Fact]
    public void A_transient_failure_is_parked_once_the_attempts_run_out()
    {
        var decision = RetryDecision.For(Transient, previousAttempts: MessagingTopology.MaxAttempts);

        Assert.Equal(FailureAction.Park, decision.Action);
        Assert.Null(decision.Tier);
        Assert.Contains("Giving up", decision.Reason, StringComparison.Ordinal);
        // The reason has to name the underlying failure, or the parked message says only that it
        // was retried - not what went wrong.
        Assert.Contains("the database blinked", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_permanent_failure_is_parked_immediately_however_many_attempts_remain(int previousAttempts)
    {
        // The whole point: a message that can never succeed must not consume the retry budget, and
        // must not delay the queue behind it for two and a half minutes to prove it.
        var decision = RetryDecision.For(Permanent, previousAttempts);

        Assert.Equal(FailureAction.Park, decision.Action);
        Assert.Null(decision.Tier);
        Assert.Contains("not retried", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("not valid JSON", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_first_delivery_has_no_attempt_header_and_counts_as_zero()
    {
        Assert.Equal(0, RetryDecision.ReadAttempt(null));
        Assert.Equal(0, RetryDecision.ReadAttempt(new Dictionary<string, object?>(StringComparer.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(HeaderShapes))]
    public void The_attempt_header_is_read_whatever_shape_the_broker_hands_it_back_in(object? raw, int expected)
    {
        // RabbitMQ does not return header values as the types they were published as - an int can
        // come back as a byte array. Reading only one shape works right up until it silently does
        // not, and then every message gets a fresh set of retries forever.
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [MessagingTopology.AttemptHeader] = raw,
        };

        Assert.Equal(expected, RetryDecision.ReadAttempt(headers));
    }

    public static TheoryData<object?, int> HeaderShapes() => new()
    {
        { 2, 2 },
        { 2L, 2 },
        { "2", 2 },
        { Encoding.UTF8.GetBytes("2"), 2 },
        { null, 0 },
        { Encoding.UTF8.GetBytes("not a number"), 0 },
        { new object(), 0 },
    };

    [Fact]
    public void An_unreadable_attempt_header_gives_the_message_its_retries_rather_than_parking_it()
    {
        // Failing open. A header this code cannot parse is this code's problem, and punishing the
        // message for it would discard work over a technicality.
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [MessagingTopology.AttemptHeader] = new object(),
        };

        var decision = RetryDecision.For(Transient, RetryDecision.ReadAttempt(headers));

        Assert.Equal(FailureAction.Retry, decision.Action);
        Assert.Equal(1, decision.Attempt);
    }
}
