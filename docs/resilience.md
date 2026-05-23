# Resilience

What happens when a dependency goes away, and what happens when this process does.

## Liveness and readiness are different questions

| Endpoint | Question | Checks |
|---|---|---|
| `/health/live` | Is this process wedged? Should it be killed and restarted? | **nothing** |
| `/health/ready` | Should traffic be sent here yet? | database, broker, outbox backlog |

**Liveness deliberately checks nothing external.** A liveness probe that fails when the database is
down causes the orchestrator to kill every replica of a perfectly healthy service during a database
outage — turning a recoverable dependency failure into a total one, and adding a cold-start storm to
an incident that was already in progress. Answering at all is the signal: it proves the process is
running, the host is accepting connections, and the thread pool is not exhausted.

Readiness checks dependencies, because an instance that cannot reach its database has nothing useful
to do with a request.

**Degraded is a 200.** The outbox backlog check reports `Degraded` past a threshold rather than
`Unhealthy`, because taking the instance out of rotation would remove the very capacity that has to
clear the backlog.

The response names each check rather than answering with the single word `Healthy`, because during
an incident the useful question is *which* dependency is down:

```json
{
  "status": "Healthy",
  "checks": {
    "database": { "status": "Healthy", "durationMs": 19.3 },
    "broker":   { "status": "Healthy", "description": "Broker reachable." },
    "outbox":   { "status": "Healthy", "description": "0 messages waiting." }
  }
}
```

The broker check opens a channel rather than reading a cached connection flag. A connection object
can report itself open while the socket underneath has quietly gone, and a consumer that believes it
is connected but receives nothing is exactly the failure this check exists to catch.

## Losing the broker mid-flight

Thirty orders were placed while `docker compose restart rabbitmq` ran underneath them:

| | |
|---|---|
| Accepted (`202`) | 30 of 30 |
| Completed | 30 of 30 |
| Receipts | 30 |
| Unpublished outbox rows afterwards | 0 |
| Dead-lettered | 0 |
| `/health/live` during the outage | `200` throughout |

Nothing was lost, and nothing needed intervention. Three mechanisms combine to produce that: the
outbox holds accepted orders that could not yet be published, `AutomaticRecoveryEnabled` and
`TopologyRecoveryEnabled` reconnect the client and re-declare its bindings, and unacknowledged
deliveries are redelivered by the broker because they were never acknowledged.

## Shutting down politely

`StopAsync` cancels the consumer **first**, then waits for work already in hand.

The order is the point. Cancelling tells the broker to send nothing more, so the set of in-flight
messages stops growing and becomes finite — only then is it worth waiting for. Closing the channel
immediately instead, which is what happens by default, drops every unacknowledged delivery. Those
messages are not lost, because the broker redelivers what was never acknowledged, but the work done
on them is thrown away and any half-processed order is processed again. Draining turns a routine
deployment from "a burst of duplicate work" into "nothing happened".

The wait is bounded by `RabbitMq:ShutdownDrainTimeout` (15s). A handler that never returns must not
stop the process exiting; the orchestrator will kill it shortly afterwards anyway, less politely.
Anything abandoned is redelivered, which is why the consumer has to be idempotent.

**Not yet proven under a real signal.** On Windows, `Stop-Process` is a hard terminate that bypasses
the graceful path entirely, so the drain never runs. The honest place to demonstrate it is Phase 15,
where `docker stop` sends `SIGTERM` and .NET runs shutdown properly.

## Prefetch

`RabbitMq:PrefetchCount`, default 10.

Prefetch is how many messages the broker will hand a consumer before it acknowledges any of them.
The trade-off runs in both directions:

- **Too low** (1) and the consumer idles for a network round trip between every message. Throughput
  becomes a function of latency to the broker rather than of how fast the work is.
- **Too high** and one consumer hoards messages its siblings could be working on, holds them all in
  memory, and turns a deploy into a large redelivery. `0` means *unlimited* in AMQP — the broker
  would hand over the entire queue.

Ten is a deliberate middle for work that takes tens of milliseconds. **No rigorous benchmark was
run**, and the number should be treated as a considered default rather than a measured optimum: the
right way to tune it is against a realistic queue depth and a realistic handler, and neither exists
outside production.
