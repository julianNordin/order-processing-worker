# Fault injection

What was deliberately broken, and what happened. Every row below was run against the real stack — the
containerised one where the fault is infrastructural, the host one where it needs a debugger's view
of timing.

A failure path that has never been exercised is a failure path nobody knows works. Several of these
found real defects; those are named.

## The matrix

| # | Fault | Expected | Observed | Verdict |
|---|---|---|---|---|
| 1 | Broker stopped, then orders placed | `202` anyway; messages held in the outbox | 3 orders accepted, 3 rows unpublished, all drained ~3s after the broker returned | ✅ |
| 2 | Broker restarted under load | Nothing lost | 30 placed across `docker compose restart rabbitmq`: 30 accepted, 30 completed, 30 receipts, 0 unpublished, 0 parked | ✅ |
| 3 | Transient failure in the handler | Retried on the ladder, then succeeds | `failed on attempt 1; retrying after 00:00:05`, receipt 7s later | ✅ |
| 4 | Handler that never succeeds | Parked after the attempts run out | 5s → 30s → 2m, then `parked after 4 attempts (3 retries)` — timings exact | ✅ |
| 5 | Malformed JSON published to the exchange | Parked immediately, **no** retries | `parked after 1 attempt(s): Permanent failure, not retried` | ✅ |
| 6 | Message with a schema version from the future | Parked immediately | Parked, 1 attempt | ✅ |
| 7 | The same message delivered twice | One receipt | 1 receipt, 1 inbox row, acknowledged not parked | ✅ |
| 8 | Duplicate forced past the short-circuit | Unique index stops it | Order reset to `Accepted`, receipt deleted, inbox row kept → `23505` → `was already processed`, **no second receipt** | ✅ |
| 9 | Wrong routing key | Loud, not silent | `MessageNotRoutedException` via `mandatory: true` + `BasicReturn` | ✅ |
| 10 | Database stopped mid-burst | Readiness fails, liveness holds, nothing half-written | `/health/ready` → `503`, `/health/live` → `200`, 0 orders without an outbox row, all completed after recovery | ✅ |
| 11 | Worker stopped with work in flight | Drains, then exits | 150 orders in flight, `docker stop` → `Stopped consuming …` → `Drain finished with 0 message(s) still in flight` | ✅ |
| 12 | Oversized order (5000 lines, 698 KB) | Handled, not rejected or parked | `202` → `Completed`, 1.1 MB receipt, DLQ unchanged | ✅ |
| 13 | Clean slate: empty volumes | Whole stack works from nothing | `docker compose down -v && up --build`: 4 healthy in ~10s, schema created by the API's migrator, smoke test passes | ✅ |

## What these found

Running the faults was not a formality. Each of these was a real defect, found by breaking something
rather than by reading the code:

**The API would not start without the broker.** `TopologyDeclarationHostedService` was an
`IHostedService`, and an `IHostedService` whose `StartAsync` throws stops the host from starting.
That defeats the entire point of the outbox — the API is supposed to keep accepting orders while the
broker is down. It is now a `BackgroundService` that retries in the background. *Found by fault 1.*

**A broker outage became database pressure.** The outbox publisher had no bounded window, so a batch
held its row locks for the full two-minute connection retry, and the `attempts` and `last_error`
columns never populated because nothing ever actually failed. `OutboxOptions.PublishTimeout` now
bounds it at 10s; the poll loop does the waiting instead. *Found by fault 1.*

**The parked reason contradicted the header.** A message parked with `x-attempt: 4` carried the
reason "Giving up after 3 attempts". Both numbers were true — four deliveries, three retries — and
an operator comparing them should not have to work that out. *Found by fault 4.*

**The healthcheck could never pass.** The aspnet runtime image ships neither `curl` nor `wget`, so the
container was reported unhealthy while being perfectly fine, which in turn blocked everything waiting
on `depends_on: condition: service_healthy`. *Found by fault 13.*

**A clean clone produced an empty database.** Nothing applied migrations in the container path: four
healthy containers, no schema. *Found by fault 13.*

## Reaching the failure paths

Neither failure path is reachable by normal use, which is exactly why they need a switch:

```bash
Faults__FailTransientlyForEmailContaining=retry-me    # fails, then recovers
Faults__SucceedAfterAttempts=1
Faults__AlwaysFailTransientlyForEmailContaining=doomed  # never recovers, exhausts the ladder
Faults__FailPermanentlyForEmailContaining=poison      # parked without consuming a retry
```

The threshold and the unconditional marker are separate settings, and that is not redundancy. A
single marker governed by `SucceedAfterAttempts` cannot be both "fails once then recovers" and "never
recovers" at the same time — which the integration suite needs, because it asserts both. Discovered
by writing a test that silently exercised the wrong path and asserted the wrong reason.

## Not tested

Stated rather than quietly omitted:

- **Broker disk alarm.** RabbitMQ blocks publishers when free disk drops below its limit. Filling a
  container's disk to prove it risks the host, and the publisher-side behaviour (a publish that
  blocks rather than fails) is documented rather than exercised. `disk_free_limit` is set to 1 GB in
  `docker-compose.yml` so the alarm means something if it ever fires.
- **Network partition between the services and the broker.** Stopping the container is a clean
  failure; a partition is a hanging one, and telling them apart needs a proxy such as Toxiproxy in
  the middle. Worth adding, not added.
- **Multiple worker replicas competing.** The prefetch and acknowledgement design assumes competing
  consumers and `FOR UPDATE SKIP LOCKED` exists to support several publishers, but only one of each
  was ever run.
- **Sustained load.** Every measurement here is from a burst of tens to low hundreds. Nothing in this
  project has been profiled, and `docs/resilience.md` says plainly that the prefetch of 10 is a
  considered default rather than a measured optimum.
