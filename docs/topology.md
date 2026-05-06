# Topology

Three exchanges, five queues. Declared in code from `MessagingTopology`, by whichever service starts
first, and identically by the other one — declaration is idempotent as long as the arguments match.

```
  Api (outbox publisher)
        │  routing key: order.placed
        ▼
  ┌──────────────────────┐
  │ exchange  orders     │  direct, durable
  └──────────┬───────────┘
             ▼
  ┌──────────────────────┐
  │ queue  orders.placed │  durable · manual ack · prefetch 10
  └──────────┬───────────┘   x-dead-letter-exchange: orders.dlx   (safety net only)
             │
     consumed by Worker
             │
   ┌─────────┴──────────────────────────────┐
   │                                        │
   ▼ transient failure                      ▼ permanent failure, or attempts exhausted
  ┌──────────────────────┐                 ┌──────────────────────┐
  │ exchange orders.retry│  direct         │ exchange orders.dlx  │  FANOUT
  └──┬────────┬────────┬─┘                 └──────────┬───────────┘
attempt.1  attempt.2  attempt.3                       ▼
   ▼         ▼         ▼                   ┌──────────────────────┐
 5s wait   30s wait   2m wait              │ queue  orders.dlq    │  parked, no consumer
   └─────────┴─────────┘                   └──────────────────────┘
        x-message-ttl per queue
        x-dead-letter-exchange: orders
        x-dead-letter-routing-key: order.placed
             │
             ▼ TTL expires → straight back to orders.placed
```

## Why three wait queues instead of one

The obvious design is a single retry queue where each message carries its own `expiration`. It does
not work, and the reason is worth knowing: **RabbitMQ only expires messages at the head of a queue.**

A queue is a queue. If a message with a two-minute TTL is at the front, a five-second message behind
it waits the full two minutes, because the broker never looks past the head to see that something
further back has expired. The delay you get is therefore not the delay you asked for — it is the
delay of whatever is in front of you.

One queue per delay removes the problem entirely: every message in `orders.retry.5s` has the same
TTL, so head-of-queue expiry and per-message expiry are the same thing.

**This was verified rather than assumed.** Publishing to `orders.retry` with routing key `attempt.1`
puts the message in `orders.retry.5s`; it is still there at three seconds and has moved to
`orders.placed` by eight.

## Why the dead-letter exchange is fanout

Every other exchange here is `direct`, because routing is an exact-match decision. `orders.dlx` is
`fanout` deliberately.

A dead letter is the last copy of a message that has already failed. Losing it to a routing-key
mismatch — the exact silent failure this whole design is trying to avoid — would be the worst
possible outcome, and a fanout exchange has no routing key to get wrong.

## Why the consumer publishes to the DLX instead of nacking

`BasicNackAsync(requeue: false)` is the idiomatic move: the broker dead-letters the message itself
and populates an `x-death` header with the queue, the reason and a count.

This code publishes to `orders.dlx` explicitly instead, then acknowledges the original. The reason is
diagnosis. `x-death` records *that* a message was rejected; it cannot record *why* — there is no
mechanism to attach an application-level reason to a nack. A parked message you cannot explain is
nearly useless, so the consumer sets `x-failure-reason`, `x-attempt` and `x-original-routing-key` on
the way in.

The cost is real and worth stating: publishing before acknowledging opens a window where the process
can die having done both the retry publish and no acknowledgement, so the message is delivered again.
That is not a flaw to hide — it is one of the reasons the consumer has to be idempotent, and it is
handled in Phase 12.

`x-dead-letter-exchange` is still set on `orders.placed` as a safety net, for rejections the broker
originates on its own.

## The queue arguments, and why changing one hurts

| Queue | Arguments |
|---|---|
| `orders.placed` | `x-dead-letter-exchange: orders.dlx` |
| `orders.retry.5s` | `x-message-ttl: 5000`, `x-dead-letter-exchange: orders`, `x-dead-letter-routing-key: order.placed` |
| `orders.retry.30s` | `x-message-ttl: 30000`, same dead-letter target |
| `orders.retry.2m` | `x-message-ttl: 120000`, same dead-letter target |
| `orders.dlq` | none — no TTL and no dead-letter target, deliberately |

`orders.dlq` has no TTL on purpose. Expiring a parked message would destroy the only evidence of why
it failed, which is the one thing that queue exists to preserve.

Redeclaring an existing queue with *different* arguments is `PRECONDITION_FAILED`, and it closes the
channel. There is no alter operation in AMQP — the queue has to be deleted first. Changing any TTL
in the table above therefore means running `scripts/rabbit-reset.ps1`, and note that
`docker compose down` without `-v` keeps the volume, so the old topology returns and the error with
it.

## Reading the broker directly

The management UI is at `http://localhost:15672` with the credentials from `.env`. The stats it
shows — queue depths in particular — are **sampled on a several-second cycle**, so a depth of 0
immediately after publishing usually means the counter has not caught up rather than that the message
went nowhere. To ask the broker a question and get an answer that is true right now, read the
messages rather than the counters:

```bash
curl -u user:pass -H "content-type: application/json" -X POST \
  http://localhost:15672/api/queues/%2F/orders.placed/get \
  -d '{"count":10,"ackmode":"reject_requeue_true","encoding":"auto"}'
```

`reject_requeue_true` puts everything back, so this is a peek rather than a consume.
