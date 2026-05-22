# Architecture

Two services and three libraries. The services never call each other — everything that passes
between them goes through the broker, and the only thing they share is a contract.

## The projects

| Project | Owns | Depends on |
|---|---|---|
| `OrderProcessing.Contracts` | The message shapes, and nothing else | **nothing at all** |
| `OrderProcessing.Persistence` | `DbContext`, entities, migrations, the outbox and inbox tables | Contracts |
| `OrderProcessing.Messaging` | Broker connection, topology declaration, publishing, consumer plumbing | Contracts |
| `OrderProcessing.Api` | HTTP: accepting orders, reporting status, serving receipts | Contracts, Persistence, Messaging |
| `OrderProcessing.Worker` | Consuming orders, generating receipts, retry and dead-letter decisions | Contracts, Persistence, Messaging |

Two of these boundaries are load-bearing enough to be asserted in
`tests/OrderProcessing.UnitTests/ArchitectureTests.cs` rather than trusted:

**`Contracts` depends on nothing.** It holds the shapes the publisher and the consumer must agree
on. The moment it acquires a dependency, that dependency has joined the contract — every consumer
now needs it too, at a compatible version. Keeping it empty is what lets the contract be copied into
a different service, or a different language, without dragging a graph behind it.

**`Api` and `Worker` do not reference each other.** They are separate processes that scale, deploy,
fail and restart independently. A project reference between them is the first step towards a shared
in-memory assumption, and the second step is a message that only works because both halves happened
to be deployed together.

## Why `Messaging` is shared but `Persistence` is not surprising

Both services reference both libraries, which looks at first like the shared-database anti-pattern.
It is a deliberate call, and the reason is drift.

The broker topology — which exchange, which routing key, which queue arguments — has to be
identical on both sides or messages vanish silently. A publisher that declares `orders.placed` and a
consumer that binds `order.placed` will not error; the publish succeeds and the message goes
nowhere. Declaring that topology from one place means the two halves cannot disagree about it.

The same argument applies to the database: the worker writes receipts and the API reads them, so
they must agree on the schema. In a system with more than one team this would be an internal API
instead. At this size, one schema owned by one project is honest.

## The worker is a web project

`OrderProcessing.Worker` uses `Microsoft.NET.Sdk.Web`, not the worker-service SDK, and that is
intentional. It runs a `BackgroundService` as its actual job, but it also needs to answer
`/health/live` and `/health/ready`. A worker that cannot be health-checked cannot be ordered
correctly in Compose, cannot be probed by a scheduler, and cannot tell you whether it is stuck or
merely idle — and "stuck or idle?" is the question you ask most often about a queue consumer.

## The outbox, and the dual write it removes

The API never publishes to the broker inside a request. It writes two rows — the order and an
`outbox_messages` row — in one `SaveChangesAsync`, and a background publisher moves that row to the
broker afterwards.

The alternative, "save the order then publish it", contains a problem no amount of error handling
fixes. They are two independent operations against two independent systems, and the process can die
between them:

| What happens | Result |
|---|---|
| Save succeeds, publish fails | The order exists and nobody will ever process it |
| Publish succeeds, save fails | The worker processes an order that does not exist |
| Process dies between the two | Either of the above, with no log line saying which |

A `try`/`catch` cannot help, because the failure mode is the process ceasing to exist between two
statements. Wrapping them in a database transaction cannot help either, because a publish is not
transactional and cannot be rolled back.

Writing the message as a row makes it part of the same commit as the order: either both exist or
neither does. **Verified rather than asserted** — with the broker stopped entirely, three orders were
placed, all answered `202`, all three rows sat unpublished in the database, and all three drained
within three seconds of the broker returning.

The price is duplicates. The publisher marks a row sent only after the broker confirms, but it can
die between the confirm and the commit, and the row is then republished. That is a deliberate trade:
losing a message is unacceptable, sending one twice is inconvenient. It is also the reason the
consumer must be idempotent, which is Phase 12.

## Idempotency: exactly-once effects on at-least-once delivery

The broker offers at-least-once delivery and nothing stronger. This system adds two more sources of
duplicates of its own, both deliberate:

- the outbox publisher can die between the broker's confirm and its own commit, so the row is still
  unsent and gets published again;
- the consumer publishes a retry copy *before* acknowledging the original, so a crash in between
  means the original is redelivered too.

Each of those chose "possibly twice" over "possibly never", which is the right way round. What is
left is to make sure a message arriving twice does not produce two receipts — and the thing made
idempotent is the **effect**, not the delivery.

`processed_messages` is keyed on the AMQP message id, and the row is written **in the same
transaction** as the receipt and the status change. A second delivery raises Postgres `23505`, which
the consumer reads as "already done", logs once, and acknowledges. It is not a failure: the effect
the message asked for has happened exactly once.

**It has to be the database.** A "have I seen this message?" query before doing the work cannot close
the race — two concurrent deliveries would both find nothing and both proceed. A unique constraint is
the only thing that can adjudicate between two transactions arriving at once.

The `Status == Completed` check in the handler is a cheap short-circuit that avoids rendering a PDF
destined for the bin. It is an optimisation, not the mechanism, and the code says so.

**Proven both ways.** Republishing an identical message is absorbed by the short-circuit. To exercise
the constraint itself, the order was reset to `Accepted` and its receipt deleted while the inbox row
was left in place: the handler then re-rendered, hit `23505` on save, logged
`was already processed`, and produced **no second receipt**. The message was acknowledged, not
dead-lettered — the dead-letter queue did not grow.

## What is not here yet

The message flow, the retry topology and the dead-letter path arrive in later phases and are
documented in `topology.md` and `retry-and-dlq.md`.
