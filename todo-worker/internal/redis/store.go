// Package redis keeps the worker's transient state: per-todo status, an in-flight lock, and the release queue.
// Redis is NOT the work queue: RabbitMQ is. If this state is lost, no message is lost (see worker.Handle).
//
// Keys
//
//	todo:{id}:status        hash   status, userId, eventId, queuedAt, processedAt, workerId, attempt, error
//	todo:{id}:lock          string held while a worker processes the todo (TTL = lease)
//	todo:release:pending    zset   todoIds waiting for release, score = enqueue time (ms)
//	todo:release:inflight   zset   todoIds claimed for release, score = claim time (ms); recovered if stale
package redis

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"time"

	"github.com/example/todo-worker/internal/todo"
	goredis "github.com/redis/go-redis/v9"
)

const (
	pendingKey  = "todo:release:pending"
	inflightKey = "todo:release:inflight"
)

func statusKey(id string) string { return "todo:" + id + ":status" }
func lockKey(id string) string   { return "todo:" + id + ":lock" }

// BeginResult is the outcome of trying to start processing a todo.
type BeginResult string

const (
	// Acquired: this worker owns the todo now.
	Acquired BeginResult = "ACQUIRED"
	// Duplicate: the todo already got past processing (waiting release or done): the message is a repeat.
	Duplicate BeginResult = "DUPLICATE"
	// Busy: another worker holds the lock; its own delivery is still unacknowledged at the broker, so this
	// copy can be acknowledged without losing work.
	Busy BeginResult = "BUSY"
)

type Store struct {
	rdb *goredis.Client
}

func New(addr, password string) *Store {
	return &Store{rdb: goredis.NewClient(&goredis.Options{
		Addr:         addr,
		Password:     password,
		DialTimeout:  3 * time.Second,
		ReadTimeout:  3 * time.Second,
		WriteTimeout: 3 * time.Second,
		MaxRetries:   1,
	})}
}

func (s *Store) Close() error { return s.rdb.Close() }

func (s *Store) Ping(ctx context.Context) error { return s.rdb.Ping(ctx).Err() }

var beginScript = goredis.NewScript(`
local st = redis.call('HGET', KEYS[1], 'status')
if st == 'WAITING_RELEASE' or st == 'PERSISTING' or st == 'COMPLETED' then return 'DUPLICATE' end
if not redis.call('SET', KEYS[2], ARGV[1], 'NX', 'PX', ARGV[2]) then return 'BUSY' end
redis.call('HSET', KEYS[1], 'status', 'PROCESSING', 'userId', ARGV[3], 'eventId', ARGV[4], 'queuedAt', ARGV[5],
           'workerId', ARGV[1], 'attempt', ARGV[6])
redis.call('HDEL', KEYS[1], 'error')
return 'ACQUIRED'
`)

// Begin atomically decides whether this delivery should be processed. It is the idempotency gate.
func (s *Store) Begin(ctx context.Context, todoID, workerID, userID, eventID string, queuedAt time.Time, attempt int, lease time.Duration) (BeginResult, error) {
	res, err := beginScript.Run(ctx, s.rdb, []string{statusKey(todoID), lockKey(todoID)},
		workerID, lease.Milliseconds(), userID, eventID, queuedAt.UTC().Format(time.RFC3339Nano), attempt).Text()
	return BeginResult(res), err
}

// Abandon releases the lock after a failed attempt so a retry (or redelivery) can acquire the todo again.
func (s *Store) Abandon(ctx context.Context, todoID, errMsg string) error {
	pipe := s.rdb.TxPipeline()
	pipe.Del(ctx, lockKey(todoID))
	pipe.HSet(ctx, statusKey(todoID), "error", errMsg)
	_, err := pipe.Exec(ctx)
	return err
}

// MarkWaitingRelease records successful processing and queues the todo for release, atomically.
func (s *Store) MarkWaitingRelease(ctx context.Context, todoID, workerID string, processedAt time.Time) error {
	pipe := s.rdb.TxPipeline()
	pipe.HSet(ctx, statusKey(todoID), "status", string(todo.StatusWaitingRelease), "workerId", workerID,
		"processedAt", processedAt.UTC().Format(time.RFC3339Nano))
	pipe.HDel(ctx, statusKey(todoID), "error")
	pipe.Del(ctx, lockKey(todoID))
	pipe.ZAddNX(ctx, pendingKey, goredis.Z{Score: float64(processedAt.UnixMilli()), Member: todoID})
	_, err := pipe.Exec(ctx)
	return err
}

// MarkFailed records permanent failure.
func (s *Store) MarkFailed(ctx context.Context, todoID, workerID, errMsg string, ttl time.Duration) error {
	pipe := s.rdb.TxPipeline()
	pipe.HSet(ctx, statusKey(todoID), "status", string(todo.StatusFailed), "workerId", workerID, "error", errMsg)
	pipe.Del(ctx, lockKey(todoID))
	pipe.Expire(ctx, statusKey(todoID), ttl)
	_, err := pipe.Exec(ctx)
	return err
}

// SetStatus updates just the status field (PERSISTING / COMPLETED); terminal states get a TTL.
func (s *Store) SetStatus(ctx context.Context, todoID string, st todo.Status, workerID string, ttl time.Duration) error {
	pipe := s.rdb.TxPipeline()
	pipe.HSet(ctx, statusKey(todoID), "status", string(st), "workerId", workerID)
	if st.Terminal() {
		pipe.Expire(ctx, statusKey(todoID), ttl)
	}
	_, err := pipe.Exec(ctx)
	return err
}

// Get returns the stored record, if any.
func (s *Store) Get(ctx context.Context, todoID string) (todo.Record, bool, error) {
	m, err := s.rdb.HGetAll(ctx, statusKey(todoID)).Result()
	if err != nil || len(m) == 0 {
		return todo.Record{}, false, err
	}
	rec := todo.Record{
		Status:   todo.Status(m["status"]),
		UserID:   m["userId"],
		EventID:  m["eventId"],
		WorkerID: m["workerId"],
		Error:    m["error"],
	}
	rec.Attempt, _ = strconv.Atoi(m["attempt"])
	rec.QueuedAt, _ = time.Parse(time.RFC3339Nano, m["queuedAt"])
	if v, ok := m["processedAt"]; ok {
		if t, err := time.Parse(time.RFC3339Nano, v); err == nil {
			rec.ProcessedAt = &t
		}
	}
	return rec, true, nil
}

var claimOneScript = goredis.NewScript(`
if redis.call('ZREM', KEYS[1], ARGV[1]) == 1 then
  redis.call('ZADD', KEYS[2], ARGV[2], ARGV[1])
  return 1
end
return 0
`)

// ClaimOne atomically takes one todo from the pending set (so a manual release and the timer can never both
// release it). The todo is parked in the in-flight set until Finish is called.
func (s *Store) ClaimOne(ctx context.Context, todoID string, now time.Time) (bool, error) {
	n, err := claimOneScript.Run(ctx, s.rdb, []string{pendingKey, inflightKey}, todoID, now.UnixMilli()).Int()
	return n == 1, err
}

var claimPendingScript = goredis.NewScript(`
local ids = redis.call('ZRANGE', KEYS[1], 0, tonumber(ARGV[1]) - 1)
for _, id in ipairs(ids) do
  redis.call('ZREM', KEYS[1], id)
  redis.call('ZADD', KEYS[2], ARGV[2], id)
end
return ids
`)

// ClaimPending atomically claims up to limit pending todos, oldest first.
func (s *Store) ClaimPending(ctx context.Context, limit int, now time.Time) ([]string, error) {
	return claimPendingScript.Run(ctx, s.rdb, []string{pendingKey, inflightKey}, limit, now.UnixMilli()).StringSlice()
}

// Finish removes a todo from the in-flight set once its release completed.
func (s *Store) Finish(ctx context.Context, todoID string) error {
	return s.rdb.ZRem(ctx, inflightKey, todoID).Err()
}

var recoverScript = goredis.NewScript(`
local ids = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
for _, id in ipairs(ids) do
  redis.call('ZREM', KEYS[1], id)
  redis.call('ZADD', KEYS[2], ARGV[2], id)
end
return #ids
`)

// RecoverStale moves releases whose worker disappeared (claimed longer than olderThan ago) back to pending.
func (s *Store) RecoverStale(ctx context.Context, now time.Time, olderThan time.Duration) (int, error) {
	cutoff := now.Add(-olderThan).UnixMilli()
	return recoverScript.Run(ctx, s.rdb, []string{inflightKey, pendingKey}, cutoff, now.UnixMilli()).Int()
}

// PendingCount is the number of todos waiting for release.
func (s *Store) PendingCount(ctx context.Context) (int64, error) {
	return s.rdb.ZCard(ctx, pendingKey).Result()
}

// IsNotFound is true for redis.Nil style misses.
func IsNotFound(err error) bool { return errors.Is(err, goredis.Nil) }

func (r BeginResult) String() string { return fmt.Sprint(string(r)) }
