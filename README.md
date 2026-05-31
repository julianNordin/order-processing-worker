# OrderProcessing

An order pipeline built around RabbitMQ. An ASP.NET Core API accepts an order and answers
immediately; a separate .NET worker service picks the order up off a queue, renders a receipt, and
stores it. The two halves never call each other.

The domain is deliberately thin — an order, some lines, a total. **The substance is everything that
can go wrong in the gap between the two services:**

- the broker is down at the moment the order is placed, and the customer must still get an answer
- the worker dies halfway through, after doing some of the work but before acknowledging any of it
- the same message is delivered twice, because at-least-once is the only delivery guarantee on offer
- a message is malformed and will never succeed, however many times it is retried

Each of those has a named answer in this repository: a transactional outbox, manual acknowledgement,
an idempotent consumer keyed on message id, and a dead-letter queue that separates *retry this later*
from *this will never work*.

## How it flows

```
POST /api/orders ──► order + outbox row written in ONE transaction
                             │
                             ▼
                     outbox publisher ──► RabbitMQ ──► worker
                                                         │
                                          receipt PDF ◄───┘
                                                         │
GET /api/orders/{id}/receipt ◄───────────────────────────┘
```

The API never publishes to the broker inside the request. It writes the message to an outbox table
in the same transaction as the order itself, and a background publisher drains that table. This is
the whole reason a broker outage cannot lose an accepted order.

## Stack

| | |
|---|---|
| Runtime | .NET 10 |
| API | ASP.NET Core minimal APIs |
| Worker | `BackgroundService` in a web host, so it can be health-checked |
| Broker | RabbitMQ, driven through `RabbitMQ.Client` directly — topology declared in code |
| Database | PostgreSQL with EF Core |
| Logging | Serilog, structured, with a correlation id that survives the queue hop |
| Receipts | QuestPDF |
| Tests | xUnit, with Testcontainers for the integration tier |
| Local stack | Docker Compose — broker, database and both services |

RabbitMQ is used through its own client rather than through a higher-level framework. The point of
the project is the messaging itself: exchanges, bindings, prefetch, acknowledgement and
dead-lettering are things this code does explicitly, not things a library hides.

## Running it

Requires Docker. Nothing else.

```bash
cp .env.example .env
docker compose up -d --build
```

Four containers: the broker, Postgres, the API on `:8080` and the worker on `:8081`. The API applies
the migrations on startup, so this works from an empty volume — verified by
`docker compose down -v && docker compose up -d`, which reaches four healthy containers and a created
schema in about ten seconds.

Place an order:

```bash
curl -i -X POST http://127.0.0.1:8080/api/orders   -H 'content-type: application/json'   -d '{"customerEmail":"buyer@example.com",
       "lines":[{"sku":"SKU-1","description":"Blue widget","quantity":3,"unitPrice":13.99}]}'
```

It answers `202 Accepted` with a `Location` header, **not** `201`. The order has been recorded; the
receipt has not been generated yet, and saying `201` would claim otherwise. Follow the `Location` to
watch the status change.

### Running the services on the host instead

Useful while developing. Start only the infrastructure, then run the two services yourself:

```bash
docker compose up -d rabbitmq postgres
export ConnectionStrings__OrderProcessing="Host=localhost;Port=5432;Database=orderprocessing;Username=orderprocessing;Password=local-development-only"
export RabbitMq__UserName=orderprocessing
export RabbitMq__Password=local-development-only
dotnet tool restore
dotnet dotnet-ef database update --project src/OrderProcessing.Persistence
dotnet run --project src/OrderProcessing.Api      # :8080
dotnet run --project src/OrderProcessing.Worker   # :8081, in another shell
```

The whole pipeline in one command:

```bash
pwsh -File scripts/smoke.ps1
# Placing an order...
#   accepted as 01a05210-... (status Accepted)
# Waiting for the worker...
#   completed
# Downloading the receipt...
#   ...orderprocessing-receipt.pdf  (76851 bytes, %PDF)
```

The broker's management UI is at `http://localhost:15672` with the credentials from `.env`.

## Following one order through both services

Every log line carries a correlation id. It is created by the API when the order is accepted, stored
on the outbox row, travels as an AMQP property, and is pushed into the worker's log context when the
message is consumed — so one query returns the whole story rather than two disconnected halves:

```
correlation id : 0HNO6FH85RNKF:00000001
message id     : 01a05215-3358-7c9e-a137-6cc65f174870

  09:51:56.401  Api     HTTP POST /api/orders responded 202 in 41.2 ms
  09:51:57.223  Api     Published outbox message 01a05215-3358-7c9e-a137-6cc65f174870
  09:51:58.758  Worker  Generated receipt for order 01a05215-32fb-7ece-8c50-e1ad02b16527 (73923 bytes)
```

Three hops, two processes, one id. `MessageId`, `OrderId` and `Redelivered` are attached the same
way, so "show me every delivery of this message" is also one query.

In Development the console is a readable template meant for a human at a terminal. Anywhere else it
is one JSON object per line on stdout, which is the only thing a container runtime collects and the
only format a log aggregator can index per-property rather than by regex.

Every log call goes through a source-generated `[LoggerMessage]` method rather than
`logger.LogInformation(...)`. The generated code checks whether the level is enabled before touching
its arguments, so a filtered-out message on the consume loop costs no boxing, no formatting and no
allocation.

## Tests

```bash
dotnet test
```

## What to read

The domain is thin on purpose. What is worth reading is the messaging:

| If you want to see | Read |
|---|---|
| Why the API never publishes inside a request | [`docs/architecture.md`](docs/architecture.md) — the outbox, and the dual write it removes |
| The exchanges, queues and bindings, and why they are shaped that way | [`docs/topology.md`](docs/topology.md) |
| How a failure is classified and what happens next | [`docs/retry-and-dlq.md`](docs/retry-and-dlq.md) |
| What survives a broker restart, and what liveness must not check | [`docs/resilience.md`](docs/resilience.md) |
| What was deliberately broken, and what broke | [`docs/fault-injection.md`](docs/fault-injection.md) |

In code, the three files that carry the design are
[`MessagingTopology`](src/OrderProcessing.Messaging/MessagingTopology.cs),
[`OutboxPublisher`](src/OrderProcessing.Api/Outbox/OutboxPublisher.cs) and
[`OrderConsumer`](src/OrderProcessing.Worker/Consuming/OrderConsumer.cs).

## Decisions worth defending

- **`202`, not `201`.** The order is recorded; the receipt is not generated yet. `201` would claim
  otherwise.
- **The raw RabbitMQ client, not a framework.** Exchanges, bindings, prefetch, acknowledgement and
  dead-lettering are things this code does explicitly. A framework would hide exactly the parts the
  project exists to show.
- **`mandatory: true` on every publish.** RabbitMQ discards an unroutable message *silently*. This
  turns the most common way a messaging system loses data into an exception.
- **Three retry queues, not one with per-message TTL.** RabbitMQ expires messages only at the head of
  a queue, so one queue would make every delay the delay of whatever is in front of it.
- **The consumer publishes to the dead-letter exchange rather than nacking.** A nack cannot carry a
  reason, and a parked message you cannot explain is nearly useless.
- **Idempotency is enforced by a unique index, not a lookup.** Two concurrent deliveries would both
  find nothing and both proceed; only the database can adjudicate that race.
- **Liveness checks nothing external.** A liveness probe that fails during a database outage gets
  every healthy replica killed at the worst possible moment.

## Roadmap

- [x] 01 — Repo skeleton, tooling, ground rules
- [x] 02 — Solution skeleton and the project boundaries
- [x] 03 — Message contracts and the versioning rule
- [x] 04 — RabbitMQ in Compose, and the topology in code
- [x] 05 — Postgres, EF Core and the order model
- [x] 06 — The API: accept an order, answer 202
- [x] 07 — The transactional outbox
- [x] 08 — The worker: consume, render the receipt
- [x] 09 — Serilog: structured logging and correlation
- [x] 10 — Retry with exponential backoff
- [x] 11 — Dead-lettering, poison messages, and the parked queue
- [x] 12 — Idempotency: exactly-once effects on at-least-once delivery
- [x] 13 — Resilience: recovery, shutdown, backpressure, health
- [x] 14 — Integration tests with Testcontainers
- [x] 15 — Containerise the whole stack
- [ ] 16 — CI, docs, fault injection, ship

## Licence

UNLICENSED — portfolio project.
