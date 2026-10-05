# Messaging contract (RabbitMQ)

The broker topology is **owned by the broker**, not by the services: `rabbitmq/definitions.json` is loaded by RabbitMQ at boot
(`load_definitions`), so neither the ASP.NET Core API nor the Go worker declares queues. One file, no drift, and a message
published before any consumer has started is not lost (the queues already exist).

> RabbitMQ does **not** seed its default user when definitions are loaded. The Aspire AppHost therefore writes the broker's
> definitions at startup (`Crash_course.AppHost/obj/rabbitmq/definitions.json`, gitignored): this committed topology **plus** the
> broker user `app` with the Aspire-generated password (only its salted SHA-256 hash is written). No credential is committed.

```
 ASP.NET Core Todo API                                    Go todo-worker
 ─────────────────────                                    ──────────────
 outbox ─► todo.events ──todo.created──► todo.processing ──► consume, process (bounded concurrency)
           (topic)                          │ failure ► todo.events / todo.retry.N ► todo.processing.retry.N
                                            │            (5 s, 30 s, 2 min TTL, then back to todo.created)
                                            │ out of retries / poison ► todo.dlx ► todo.processing.dlq
 outbox ─► todo.events ──todo.release──► todo.release ─────► consume admin release commands
 consume ◄─ todo.status ◄─todo.status.changed── todo.events ◄── worker reports status transitions
```

| Exchange | Type | Notes |
|---|---|---|
| `todo.events` | topic, durable | all business messages |
| `todo.dlx` | direct, durable | dead letters |

| Queue | Bound as | Consumer | Dead letters to |
|---|---|---|---|
| `todo.processing` | `todo.events` / `todo.created` | Go | `todo.processing.dlq` |
| `todo.processing.retry.1/2/3` | `todo.events` / `todo.retry.1/2/3` | none (TTL 5 s / 30 s / 2 min, then dead-letter back to `todo.created`) | - |
| `todo.release` | `todo.events` / `todo.release` | Go | `todo.release.dlq` |
| `todo.status` | `todo.events` / `todo.status.changed` | ASP.NET | `todo.status.dlq` |

**Retry backoff** is the TTL of the three delay queues (one queue per retry attempt, so a long delay never blocks a short one).
Change it by editing `definitions.json`. The worker's `MAX_RETRIES` (0-3) chooses how many tiers are used.

All messages: `application/json`, persistent, AMQP `message_id` = `eventId`, `type` = `eventType`. Consumers use manual acks and are
**at-least-once**, so every handler is idempotent. Consumers are tolerant readers (unknown fields are ignored).

## Messages

`TodoCreated` (API → worker). Identifiers and metadata only; todo content never goes over the broker:
```json
{ "eventId": "uuid", "eventType": "TodoCreated", "todoId": "uuid", "userId": "uuid", "createdAt": "2026-10-04T12:00:00Z" }
```

`ReleaseRequested` (API → worker). Sent **only after the API authorized an ADMIN**. `todoId` is absent for "release everything waiting":
```json
{ "eventId": "uuid", "eventType": "ReleaseRequested", "scope": "single|all", "todoId": "uuid", "requestedBy": "admin-user-id", "requestedAt": "..." }
```

`TodoStatusChanged` (worker → API). The worker *reports*; the API (owner of the Todo domain) validates the transition and persists it:
```json
{ "eventId": "uuid", "eventType": "TodoStatusChanged", "todoId": "uuid", "userId": "uuid",
  "status": "PROCESSING|WAITING_RELEASE|PERSISTING|COMPLETED|FAILED", "workerId": "worker-01",
  "occurredAt": "...", "error": "only for FAILED, short and sanitized" }
```

AMQP header `x-retry-count` carries the retry attempt on messages re-published to a delay queue.

## Processing states (state machine owned by the Todo API)

```
QUEUED ─► PROCESSING ─► WAITING_RELEASE ─(manual release | timer)─► PERSISTING ─► COMPLETED
              └──────────────► FAILED (any non-terminal state; after retries)       
FAILED ─(admin Retry, an explicit API call, not a worker report)─► QUEUED
```
Reports only move **forward** (a late/duplicated message never moves a todo backwards; skipping a step is tolerated), and
COMPLETED/FAILED are final for reports. Every transition is one atomic SQL `UPDATE ... WHERE status IN (allowed sources)`.
