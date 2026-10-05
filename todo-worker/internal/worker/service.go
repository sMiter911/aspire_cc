// Package worker contains the job logic: consuming TodoCreated messages, processing them with bounded concurrency,
// reporting status, and releasing finished jobs for persistence by the Todo API.
package worker

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"sync"
	"time"

	"github.com/example/todo-worker/internal/rabbitmq"
	redisstore "github.com/example/todo-worker/internal/redis"
	"github.com/example/todo-worker/internal/todo"
)

// Store is the Redis-backed transient state (see internal/redis).
type Store interface {
	Begin(ctx context.Context, todoID, workerID, userID, eventID string, queuedAt time.Time, attempt int, lease time.Duration) (redisstore.BeginResult, error)
	Abandon(ctx context.Context, todoID, errMsg string) error
	MarkWaitingRelease(ctx context.Context, todoID, workerID string, processedAt time.Time) error
	MarkFailed(ctx context.Context, todoID, workerID, errMsg string, ttl time.Duration) error
	SetStatus(ctx context.Context, todoID string, st todo.Status, workerID string, ttl time.Duration) error
	Get(ctx context.Context, todoID string) (todo.Record, bool, error)
	ClaimOne(ctx context.Context, todoID string, now time.Time) (bool, error)
	ClaimPending(ctx context.Context, limit int, now time.Time) ([]string, error)
	Finish(ctx context.Context, todoID string) error
	RecoverStale(ctx context.Context, now time.Time, olderThan time.Duration) (int, error)
}

// Publisher sends confirmed messages to the broker.
type Publisher interface {
	Publish(ctx context.Context, exchange, key string, p rabbitmq.Publishing) error
}

// Job is what a Processor works on.
type Job struct {
	EventID, TodoID, UserID string
	Attempt                 int
}

// Processor does the actual (here: simulated) work for one todo.
type Processor interface {
	Process(ctx context.Context, job Job) error
}

// Gate lets handlers wait for a dependency to come back instead of spinning on requeues.
type Gate interface {
	// Wait blocks until the dependency is healthy, ctx ends or max elapses; it returns whether it is healthy.
	Wait(ctx context.Context, max time.Duration) bool
}

type Options struct {
	WorkerID       string
	Concurrency    int
	ProcessTimeout time.Duration
	MaxRetries     int
	ReleaseTimeout time.Duration
	ReleaseBatch   int
	StatusTTL      time.Duration
	// DependencyWait bounds how long a handler waits for Redis to return before handing the message back.
	DependencyWait time.Duration
	// BusyPause is how long a handler waits before returning a message whose todo is locked by another worker.
	BusyPause time.Duration
}

type Service struct {
	opts  Options
	store Store
	pub   Publisher
	proc  Processor
	gate  Gate
	log   *slog.Logger
	now   func() time.Time
}

func New(opts Options, store Store, pub Publisher, proc Processor, gate Gate, log *slog.Logger) *Service {
	if opts.DependencyWait == 0 {
		opts.DependencyWait = 30 * time.Second
	}
	if opts.BusyPause == 0 {
		opts.BusyPause = 500 * time.Millisecond
	}
	return &Service{opts: opts, store: store, pub: pub, proc: proc, gate: gate, log: log, now: time.Now}
}

// publicError is implemented by errors whose message is safe to show to users and store in the Todo API.
type publicError interface{ Public() string }

// sanitize turns an arbitrary error into text that is safe to persist and show (no internals, bounded length).
func sanitize(err error) string {
	var pe publicError
	switch {
	case errors.Is(err, context.DeadlineExceeded):
		return "processing timed out"
	case errors.As(err, &pe):
		s := pe.Public()
		if len(s) > 200 {
			s = s[:200]
		}
		return s
	}
	return "processing failed"
}

// decode is a tolerant reader: unknown fields are ignored so producers can add fields without breaking consumers.
func decode(body []byte, v any) error { return json.Unmarshal(body, v) }

// HandleCreated processes one TodoCreated delivery.
//
// Delivery semantics are at-least-once, so everything here is idempotent:
//   - Redis Begin() is an atomic gate: a todo already past PROCESSING (or locked by another worker) is a duplicate
//     and is acknowledged without being processed again.
//   - Status events are re-publishable: the Todo API ignores transitions it has already applied.
//   - The message is acknowledged only after the todo is safely recorded as WAITING_RELEASE, so a crash at any
//     earlier point makes RabbitMQ redeliver it.
func (s *Service) HandleCreated(ctx context.Context, d rabbitmq.Delivery) rabbitmq.Outcome {
	var msg todo.Created
	if err := decode(d.Body, &msg); err != nil || msg.TodoID == "" || msg.UserID == "" || msg.EventType != todo.EventTodoCreated {
		// Malformed: no retry can fix it. Reject => dead-letter queue.
		s.log.Error("malformed TodoCreated message, dead-lettering", "message_id", d.MessageID, "size", len(d.Body))
		return rabbitmq.Reject
	}
	attempt := d.RetryCount()
	started := s.now()
	log := s.log.With("job_id", msg.EventID, "todo_id", msg.TodoID, "worker_id", s.opts.WorkerID, "attempt", attempt)

	if !s.gate.Wait(ctx, s.opts.DependencyWait) {
		log.Warn("redis unavailable, returning message to the broker")
		return rabbitmq.Requeue
	}

	// Lease: a live holder always finishes (or times out) inside it; a crashed holder is taken over after it.
	lease := s.opts.ProcessTimeout + 5*time.Second
	res, err := s.store.Begin(ctx, msg.TodoID, s.opts.WorkerID, msg.UserID, msg.EventID, msg.CreatedAt, attempt, lease)
	if err != nil {
		log.Warn("redis error while starting job, returning message to the broker", "error", err)
		return rabbitmq.Requeue
	}
	switch res {
	case redisstore.Duplicate:
		log.Info("duplicate message ignored", "status", "duplicate")
		return rabbitmq.Ack
	case redisstore.Busy:
		// Another worker holds the lock. We must NOT ack: if that worker crashed, this delivery is the only copy
		// of the work (the broker returned the unacknowledged original to us). Hand it back after a short pause;
		// once the holder finishes this becomes a Duplicate (ack), and if it died the lease expires and we take over.
		log.Info("todo is locked by another worker, returning message to the broker", "status", "busy")
		select {
		case <-time.After(s.opts.BusyPause):
		case <-ctx.Done():
		}
		return rabbitmq.Requeue
	}

	log.Info("job started", "started_at", started.UTC().Format(time.RFC3339Nano))
	if err := s.reportStatus(ctx, msg.TodoID, msg.UserID, todo.StatusProcessing, ""); err != nil {
		_ = s.store.Abandon(ctx, msg.TodoID, "")
		log.Warn("could not report PROCESSING, returning message to the broker", "error", err)
		return rabbitmq.Requeue
	}

	procCtx, cancel := context.WithTimeout(ctx, s.opts.ProcessTimeout)
	err = s.proc.Process(procCtx, Job{EventID: msg.EventID, TodoID: msg.TodoID, UserID: msg.UserID, Attempt: attempt})
	cancel()
	if err != nil {
		return s.onFailure(ctx, d, msg, attempt, started, err)
	}

	// Report first, then record in Redis: if we crash in between, the redelivery re-reports (harmless) and
	// the todo is never stuck in the API without a matching entry in the release queue.
	processedAt := s.now()
	if err := s.reportStatus(ctx, msg.TodoID, msg.UserID, todo.StatusWaitingRelease, ""); err != nil {
		_ = s.store.Abandon(ctx, msg.TodoID, "")
		log.Warn("could not report WAITING_RELEASE, returning message to the broker", "error", err)
		return rabbitmq.Requeue
	}
	if err := s.store.MarkWaitingRelease(ctx, msg.TodoID, s.opts.WorkerID, processedAt); err != nil {
		log.Warn("could not queue todo for release, returning message to the broker", "error", err)
		return rabbitmq.Requeue
	}

	log.Info("job finished",
		"started_at", started.UTC().Format(time.RFC3339Nano),
		"completed_at", processedAt.UTC().Format(time.RFC3339Nano),
		"duration_ms", processedAt.Sub(started).Milliseconds(),
		"status", string(todo.StatusWaitingRelease))
	return rabbitmq.Ack
}

func (s *Service) onFailure(ctx context.Context, d rabbitmq.Delivery, msg todo.Created, attempt int, started time.Time, cause error) rabbitmq.Outcome {
	reason := sanitize(cause)
	log := s.log.With("job_id", msg.EventID, "todo_id", msg.TodoID, "worker_id", s.opts.WorkerID, "attempt", attempt)
	_ = s.store.Abandon(ctx, msg.TodoID, reason) // free the lock so the retry can acquire the todo

	if attempt < s.opts.MaxRetries {
		next := attempt + 1
		err := s.pub.Publish(ctx, todo.Exchange, fmt.Sprintf("%s%d", todo.RetryKeyPrefix, next), rabbitmq.Publishing{
			MessageID: d.MessageID, Type: todo.EventTodoCreated, Body: d.Body,
			Headers: map[string]any{"x-retry-count": int32(next)},
		})
		if err != nil {
			log.Warn("could not schedule retry, returning message to the broker", "error", err)
			return rabbitmq.Requeue
		}
		log.Warn("job failed, retry scheduled", "status", "retry", "next_attempt", next, "reason", reason,
			"duration_ms", s.now().Sub(started).Milliseconds())
		return rabbitmq.Ack // the retry copy now carries the work
	}

	// Out of retries: tell the API, then dead-letter the original message for inspection.
	if err := s.reportStatus(ctx, msg.TodoID, msg.UserID, todo.StatusFailed, reason); err != nil {
		log.Warn("could not report FAILED, returning message to the broker", "error", err)
		return rabbitmq.Requeue
	}
	_ = s.store.MarkFailed(ctx, msg.TodoID, s.opts.WorkerID, reason, s.opts.StatusTTL)
	log.Error("job failed permanently, dead-lettering", "status", string(todo.StatusFailed), "reason", reason,
		"duration_ms", s.now().Sub(started).Milliseconds())
	return rabbitmq.Reject
}

// HandleRelease processes an admin release command. The command is trusted only because it arrived through the
// broker from the Todo API, which authorized the ADMIN before publishing it. The worker never inspects who
// "claims" to be an admin.
func (s *Service) HandleRelease(ctx context.Context, d rabbitmq.Delivery) rabbitmq.Outcome {
	var cmd todo.ReleaseRequested
	if err := decode(d.Body, &cmd); err != nil || cmd.EventType != todo.EventReleaseRequested ||
		(cmd.Scope != todo.ScopeAll && !(cmd.Scope == todo.ScopeSingle && cmd.TodoID != "")) {
		s.log.Error("malformed ReleaseRequested message, dead-lettering", "message_id", d.MessageID)
		return rabbitmq.Reject
	}
	log := s.log.With("job_id", cmd.EventID, "worker_id", s.opts.WorkerID, "scope", cmd.Scope, "requested_by", cmd.RequestedBy)
	if !s.gate.Wait(ctx, s.opts.DependencyWait) {
		return rabbitmq.Requeue
	}

	if cmd.Scope == todo.ScopeSingle {
		ok, err := s.store.ClaimOne(ctx, cmd.TodoID, s.now())
		if err != nil {
			log.Warn("redis error while claiming release, returning message to the broker", "error", err)
			return rabbitmq.Requeue
		}
		if !ok {
			// Already released, never reached WAITING_RELEASE, or lost with Redis: nothing to do. Idempotent.
			log.Info("release ignored: todo is not pending", "todo_id", cmd.TodoID)
			return rabbitmq.Ack
		}
		if err := s.release(ctx, cmd.TodoID); err != nil {
			log.Warn("release failed, will be retried by the recovery sweep", "todo_id", cmd.TodoID, "error", err)
		}
		return rabbitmq.Ack
	}

	n, err := s.ReleasePending(ctx)
	if err != nil {
		log.Warn("release-all failed", "error", err)
		return rabbitmq.Requeue
	}
	log.Info("release-all done", "released", n)
	return rabbitmq.Ack
}

// ReleasePending claims every pending todo (up to the batch size) and releases them with bounded concurrency.
// It is the single code path for the timer and for "release all".
func (s *Service) ReleasePending(ctx context.Context) (int, error) {
	ids, err := s.store.ClaimPending(ctx, s.opts.ReleaseBatch, s.now())
	if err != nil {
		return 0, err
	}
	sem := make(chan struct{}, s.opts.Concurrency)
	var wg sync.WaitGroup
	var mu sync.Mutex
	done := 0
	for _, id := range ids {
		sem <- struct{}{}
		wg.Add(1)
		go func(id string) {
			defer func() { <-sem; wg.Done() }()
			if err := s.release(ctx, id); err != nil {
				s.log.Warn("release failed, will be retried by the recovery sweep", "todo_id", id, "error", err)
				return
			}
			mu.Lock()
			done++
			mu.Unlock()
		}(id)
	}
	wg.Wait()
	return done, nil
}

// RecoverStale puts releases whose worker vanished back into the pending set.
func (s *Service) RecoverStale(ctx context.Context) (int, error) {
	return s.store.RecoverStale(ctx, s.now(), s.opts.ReleaseTimeout)
}

// release moves one claimed todo PERSISTING -> COMPLETED. The Todo API persists each reported state. On any
// failure the todo stays in the in-flight set and RecoverStale hands it back; every step is repeatable.
func (s *Service) release(ctx context.Context, todoID string) error {
	started := s.now()
	rec, _, err := s.store.Get(ctx, todoID)
	if err != nil {
		return err
	}
	if err := s.reportStatus(ctx, todoID, rec.UserID, todo.StatusPersisting, ""); err != nil {
		return err
	}
	if err := s.store.SetStatus(ctx, todoID, todo.StatusPersisting, s.opts.WorkerID, s.opts.StatusTTL); err != nil {
		return err
	}
	if err := s.reportStatus(ctx, todoID, rec.UserID, todo.StatusCompleted, ""); err != nil {
		return err
	}
	if err := s.store.SetStatus(ctx, todoID, todo.StatusCompleted, s.opts.WorkerID, s.opts.StatusTTL); err != nil {
		return err
	}
	if err := s.store.Finish(ctx, todoID); err != nil {
		return err
	}
	done := s.now()
	s.log.Info("job released", "todo_id", todoID, "worker_id", s.opts.WorkerID,
		"started_at", started.UTC().Format(time.RFC3339Nano), "completed_at", done.UTC().Format(time.RFC3339Nano),
		"duration_ms", done.Sub(started).Milliseconds(), "status", string(todo.StatusCompleted))
	return nil
}

// reportStatus tells the Todo API about a transition. The API owns the state machine and the database.
func (s *Service) reportStatus(ctx context.Context, todoID, userID string, st todo.Status, errText string) error {
	evt := todo.StatusChanged{
		EventID: todo.NewID(), EventType: todo.EventStatusChanged, TodoID: todoID, UserID: userID,
		Status: st, WorkerID: s.opts.WorkerID, OccurredAt: s.now().UTC(), Error: errText,
	}
	body, err := json.Marshal(evt)
	if err != nil {
		return err
	}
	return s.pub.Publish(ctx, todo.Exchange, todo.KeyStatusChanged, rabbitmq.Publishing{
		MessageID: evt.EventID, Type: todo.EventStatusChanged, Body: body,
	})
}
