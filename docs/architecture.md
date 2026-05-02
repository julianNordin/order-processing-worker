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

## What is not here yet

The message flow, the retry topology and the dead-letter path arrive in later phases and are
documented in `topology.md` and `retry-and-dlq.md`.
