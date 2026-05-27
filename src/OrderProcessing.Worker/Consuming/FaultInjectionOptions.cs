namespace OrderProcessing.Worker.Consuming;

/// <summary>
/// A deliberate way to make processing fail, so the retry ladder and the dead-letter path can be
/// demonstrated and tested without breaking anything real.
///
/// Off unless configured. This is not a test hook bolted on afterwards - a failure path that can
/// only be exercised by genuinely breaking the database is a failure path nobody exercises, and
/// therefore one nobody knows works. Being able to say "fail this specific order, transiently"
/// turns Phases 10 and 11 into something observable rather than something asserted.
/// </summary>
public sealed class FaultInjectionOptions
{
    public const string SectionName = "Faults";

    /// <summary>
    /// Orders whose customer email contains this string fail with a TRANSIENT error, so they go
    /// round the backoff ladder. Empty disables it.
    /// </summary>
    public string FailTransientlyForEmailContaining { get; set; } = "";

    /// <summary>
    /// Orders whose customer email contains this string fail PERMANENTLY, so they are parked
    /// immediately without using any retries. Empty disables it.
    /// </summary>
    public string FailPermanentlyForEmailContaining { get; set; } = "";

    /// <summary>
    /// Orders whose customer email contains this string fail transiently on EVERY attempt,
    /// regardless of <see cref="SucceedAfterAttempts"/>. Empty disables it.
    ///
    /// Separate from <see cref="FailTransientlyForEmailContaining"/> so that one configuration can
    /// arm both behaviours at once: a suite needs "fails once then recovers" and "never recovers"
    /// simultaneously, and a single marker governed by an attempt threshold cannot be both.
    /// </summary>
    public string AlwaysFailTransientlyForEmailContaining { get; set; } = "";

    /// <summary>
    /// Stop failing transiently after this many attempts, so a message can be seen to fail, retry
    /// and then SUCCEED. Zero means always fail, which is how retry exhaustion is demonstrated.
    /// </summary>
    public int SucceedAfterAttempts { get; set; }
}
