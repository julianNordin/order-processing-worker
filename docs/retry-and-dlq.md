# Retry, backoff and the dead-letter queue

Everything here follows from one question asked of every failure: **will trying again ever help?**

## Transient or permanent

| Failure | Kind | What happens |
|---|---|---|
| Database briefly unreachable | transient | 5s → 30s → 2m, then parked |
| Timeout, broker hiccup | transient | as above |
| Body is not valid JSON | **permanent** | parked immediately, no retries |
| Required field missing | **permanent** | parked immediately |
| `SchemaVersion` newer than this build | **permanent** | parked immediately |
| No such order | **permanent** | parked immediately |

The consumer signals the second group by throwing `PermanentMessageFailureException`; anything else
is treated as transient.

**Retrying everything is the mistake that looks safe.** A malformed message retried three times over
two and a half minutes is still malformed at the end, and every message queued behind it has waited
for the privilege. One bad message becomes a slow, repeating, self-inflicted outage — and because
each attempt logs the same failure, it also buries the log.

The reverse mistake is cheaper but still real: treating a transient failure as permanent throws away
an order because a database connection blinked.

## The ladder

Three wait queues, not one queue with per-message `expiration`:

```
orders.placed ──(transient failure)──► orders.retry ──attempt.1──► orders.retry.5s   (ttl 5s)
                                                    ──attempt.2──► orders.retry.30s  (ttl 30s)
                                                    ──attempt.3──► orders.retry.2m   (ttl 120s)
                                                                        │
                                        each dead-letters back to ──────┘
                                        exchange `orders`, key `order.placed`
```

**Why three queues.** RabbitMQ expires messages **only at the head of a queue**. Put a two-minute
message and a five-second message in the same queue and the five-second one waits behind the
two-minute one, because the broker never looks past the head to notice that something further back
is due. The delay you get is not the delay you asked for; it is the delay of whatever is in front of
you. One queue per delay makes head-of-queue expiry and per-message expiry the same thing.

## A retry is a publish, not a nack

On a transient failure the consumer **publishes a copy** to `orders.retry` with an incremented
`x-attempt` header, then **acknowledges the original**.

Nacking would dead-letter the message immediately: there is no way to delay it, and no way to record
which attempt this was. Publishing lets the wait queue's TTL provide the delay and lets the attempt
counter travel with the message.

**Publish first, then acknowledge — the order is deliberate.** A crash between the two means the
original is redelivered and the work happens twice. That is a duplicate, and duplicates are
survivable (Phase 12 makes the effect idempotent). The other order would mean a crash loses the
message entirely, which is not survivable.

**The retry reuses the original `MessageId`.** It is the deduplication key; a retry that generated a
fresh id would look like a different message and defeat idempotency completely.

## The parked queue

`orders.dlq` has no consumer, no TTL and no dead-letter target. A parked message stays parked until a
person decides what to do with it — expiring it would destroy the only evidence of why it failed.

Messages arrive carrying the headers that make them diagnosable:

| Header | Contents |
|---|---|
| `x-attempt` | How many attempts were made |
| `x-failure-reason` | What went wrong, in words, including the underlying exception |
| `x-original-routing-key` | The key it arrived on before being parked |

This is why the consumer publishes to `orders.dlx` rather than nacking. A nack lets the broker
populate `x-death`, which records *that* a message was rejected but has no mechanism to record
*why*. A parked message you cannot explain is very nearly useless.

`orders.dlx` is a **fanout** exchange while everything else here is `direct`. A dead letter is the
last copy of a message that has already failed once; losing it to a routing-key mismatch would be
the worst possible outcome, and fanout has no routing key to get wrong.

## Exercising it

Neither path is reachable by normal use, and a failure path nobody exercises is a failure path nobody
knows works. `FaultInjectionOptions` makes both reachable on demand:

```bash
# Fail the first attempt, then succeed: proves the ladder recovers.
Faults__FailTransientlyForEmailContaining=retry-me
Faults__SucceedAfterAttempts=1

# Never succeed: proves retry exhaustion ends in the dead-letter queue.
Faults__FailTransientlyForEmailContaining=doomed
Faults__SucceedAfterAttempts=0

# Fail permanently: proves a poison message is parked without consuming any retries.
Faults__FailPermanentlyForEmailContaining=poison
```

Both were run rather than assumed:

```
# transient, configured to succeed on the second attempt
[11:57:54 WRN] Message 01a0521a-... failed on attempt 1; retrying after 00:00:05
[11:58:01 INF] Generated receipt for order 01a0521a-... (66184 bytes)
                                                     ^ 7 seconds later, one 5s tier

# permanent
[11:58:44 ERR] Message 01a0521b-... parked after 1 attempt(s): Permanent failure, not retried: ...
  orders.dlq: 1 message
    x-attempt: 1
    x-failure-reason: Permanent failure, not retried: Fault injection: ...
    x-original-routing-key: order.placed
```

Note the attempt count on the parked poison message: **1**, not 3. It was never retried.
