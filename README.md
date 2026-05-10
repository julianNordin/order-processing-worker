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

Requires Docker and the .NET 10 SDK. The whole stack moves into Compose in Phase 15; until then the
broker and database run in containers and the services run on the host.

```bash
cp .env.example .env
docker compose up -d                 # rabbitmq + postgres, ~10s to healthy

# Credentials come from the environment and are never committed.
export ConnectionStrings__OrderProcessing="Host=localhost;Port=5432;Database=orderprocessing;Username=orderprocessing;Password=local-development-only"
export RabbitMq__UserName=orderprocessing
export RabbitMq__Password=local-development-only

dotnet tool restore                  # pinned dotnet-ef
dotnet dotnet-ef database update --project src/OrderProcessing.Persistence
dotnet run --project src/OrderProcessing.Api      # http://127.0.0.1:8080
```

Place an order:

```bash
curl -i -X POST http://127.0.0.1:8080/api/orders   -H 'content-type: application/json'   -d '{"customerEmail":"buyer@example.com",
       "lines":[{"sku":"SKU-1","description":"Blue widget","quantity":3,"unitPrice":13.99}]}'
```

It answers `202 Accepted` with a `Location` header, **not** `201`. The order has been recorded; the
receipt has not been generated yet, and saying `201` would claim otherwise. Follow the `Location` to
watch the status change.

The broker's management UI is at `http://localhost:15672` with the credentials from `.env`.

## Tests

```bash
dotnet test
```

## Roadmap

- [x] 01 — Repo skeleton, tooling, ground rules
- [x] 02 — Solution skeleton and the project boundaries
- [x] 03 — Message contracts and the versioning rule
- [x] 04 — RabbitMQ in Compose, and the topology in code
- [x] 05 — Postgres, EF Core and the order model
- [x] 06 — The API: accept an order, answer 202
- [ ] 07 — The transactional outbox
- [ ] 08 — The worker: consume, render the receipt
- [ ] 09 — Serilog: structured logging and correlation
- [ ] 10 — Retry with exponential backoff
- [ ] 11 — Dead-lettering, poison messages, and the parked queue
- [ ] 12 — Idempotency: exactly-once effects on at-least-once delivery
- [ ] 13 — Resilience: recovery, shutdown, backpressure, health
- [ ] 14 — Integration tests with Testcontainers
- [ ] 15 — Containerise the whole stack
- [ ] 16 — CI, docs, fault injection, ship

## Licence

UNLICENSED — portfolio project.
