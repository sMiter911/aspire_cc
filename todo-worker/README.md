# todo-worker (Go)

Asynchronous todo processing. It consumes `TodoCreated` from RabbitMQ, processes jobs concurrently (bounded), tracks transient
state in Redis, reports status transitions back through RabbitMQ and releases finished jobs on a timer or on an admin command.

**It does not own the Todo domain**: no database, no user authentication, no public API (only `/health` and `/ready`). The ASP.NET Core
Todo API validates and persists every status the worker reports, and decides who may trigger a release.

```
cmd/worker           entrypoint: wiring, signals, graceful shutdown
internal/config      environment configuration + validation
internal/todo        message contract (Created, ReleaseRequested, StatusChanged) and statuses
internal/rabbitmq    self-healing connection, confirmed publishing, consumer + bounded dispatch (Pool)
internal/redis       job state: status hash, lock lease, release queue (Lua scripts for atomic claims)
internal/worker      job logic: idempotent handling, retries, DLQ, release
internal/scheduler   periodic release + stale-release recovery
internal/health      /health, /ready and the Redis availability gate
internal/integration end-to-end tests against real RabbitMQ + Redis (Testcontainers)
```

## Concurrency

`WORKER_CONCURRENCY` (default 5) is a hard cap *and* the consumer prefetch. A `Pool` (buffered channel as a counting semaphore + `WaitGroup`)
starts one goroutine per delivery, never more than the cap; the reader blocks when the pool is full, so excess messages wait **in
RabbitMQ**, not in memory. The pool outlives AMQP channels: after a reconnect the new consumer shares the same slots, and a job stuck on a dead channel
keeps its slot until it finishes or hits `PROCESS_TIMEOUT`. On shutdown (SIGINT/SIGTERM) no new job is started, deliveries already pushed to the
worker are returned to the broker, and running jobs finish (bounded by `PROCESS_TIMEOUT`).

Observe it: `python scripts/demo.py --url <frontend-url> --count 20` shows 20 todos processed in bursts of 5; the worker logs one JSON line per job with
`job_id, todo_id, worker_id, started_at, completed_at, duration_ms, status`.

## Delivery, idempotency, failures

| Situation | Behavior |
|---|---|
| Duplicate delivery of a processed todo | Redis `Begin` (atomic Lua) sees WAITING_RELEASE/PERSISTING/COMPLETED → acked, no reprocessing |
| Same todo delivered to two workers at once | the second sees the lock → message handed back after a short pause (never acked: if the holder crashed this is the only copy). When the holder finishes it becomes a duplicate (ack); if it died the lock lease expires and the todo is taken over |
| Worker crash mid-job | message unacknowledged → RabbitMQ redelivers; lock lease (`PROCESS_TIMEOUT`+5 s) expires; another attempt completes it |
| Processing error / timeout | released lock, copy published to `todo.retry.N` (5 s / 30 s / 2 min), original acked. After `MAX_RETRIES`: report FAILED, then reject → dead-letter queue |
| Malformed message | rejected straight to the DLQ |
| Redis unavailable | handler waits (up to 30 s) for the health gate, then returns the message to RabbitMQ untouched; `/ready` = 503. Nothing is acked, nothing lost |
| RabbitMQ unavailable | reconnect loop with backoff; consumers re-attach; publishes fail loudly (never silently) and the message stays with the broker/Redis state |
| Status cannot be reported | message not acked; todo stays retryable (lock released) |
| Release interrupted | claimed todos sit in `todo:release:inflight`; the sweep moves stale ones back to pending, so a crashed releaser loses nothing |

Publishing uses publisher confirms with `mandatory`: an unroutable or unconfirmed message is an error, not a silent drop.

## Redis keys

`todo:{id}:status` (hash: status, userId, eventId, queuedAt, processedAt, workerId, attempt, error; terminal states expire after `STATUS_TTL`),
`todo:{id}:lock` (lease), `todo:release:pending` (zset by enqueue time), `todo:release:inflight` (zset by claim time).
Redis is **not** the work queue: if it is wiped, RabbitMQ still holds every unprocessed message.

## Configuration (environment)

| Variable | Default | |
|---|---|---|
| `AMQP_HOST/PORT/USER/PASSWORD/VHOST` | localhost / 5672 / - / - / `/` | set by Aspire |
| `REDIS_ADDR`, `REDIS_PASSWORD` | localhost:6379 | set by Aspire |
| `WORKER_ID` | `worker-<host>-<pid>` | |
| `WORKER_CONCURRENCY` | 5 | |
| `MAX_RETRIES` | 3 (max 3 = number of delay queues) | |
| `RELEASE_INTERVAL` | 5m (Aspire dev: 1m) | timer release; first tick after one interval |
| `RELEASE_TIMEOUT` / `RELEASE_BATCH` | 2m / 100 | stale-release recovery / batch size |
| `PROCESS_TIMEOUT` | 30s | per job |
| `PROCESS_DURATION` / `PROCESS_FAIL_RATE` | 3s / 0 | simulated work for demos (swap `worker.Processor` for real work) |
| `STATUS_TTL` | 24h | |
| `PORT` | 8081 | health endpoints |

## Run and test

```bash
cd todo-worker
go test ./... -short          # unit tests only (no Docker)
go test ./... -count=1        # + integration tests (Docker; real RabbitMQ + Redis, ~45 s)
```
Normally started by Aspire (`go run ./cmd/worker`). Needs Go 1.26+.
