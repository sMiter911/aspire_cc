package worker

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/example/todo-worker/internal/rabbitmq"
	redisstore "github.com/example/todo-worker/internal/redis"
	"github.com/example/todo-worker/internal/todo"
)

// ---- fakes -------------------------------------------------------------------------------------------

type published struct {
	Exchange, Key string
	P             rabbitmq.Publishing
}

type fakePublisher struct {
	mu   sync.Mutex
	msgs []published
	fail func(key string) error
}

func (f *fakePublisher) Publish(_ context.Context, ex, key string, p rabbitmq.Publishing) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.fail != nil {
		if err := f.fail(key); err != nil {
			return err
		}
	}
	f.msgs = append(f.msgs, published{ex, key, p})
	return nil
}

// statuses returns the sequence of reported statuses for a todo.
func (f *fakePublisher) statuses(todoID string) []todo.Status {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []todo.Status
	for _, m := range f.msgs {
		if m.Key != todo.KeyStatusChanged {
			continue
		}
		var e todo.StatusChanged
		_ = json.Unmarshal(m.P.Body, &e)
		if e.TodoID == todoID {
			out = append(out, e.Status)
		}
	}
	return out
}

func (f *fakePublisher) byKey(key string) []published {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []published
	for _, m := range f.msgs {
		if m.Key == key {
			out = append(out, m)
		}
	}
	return out
}

type fakeProcessor struct {
	mu    sync.Mutex
	calls int
	err   func(call int) error
}

func (p *fakeProcessor) Process(ctx context.Context, _ Job) error {
	p.mu.Lock()
	p.calls++
	n := p.calls
	p.mu.Unlock()
	if p.err != nil {
		return p.err(n)
	}
	return nil
}

type gate struct{ healthy bool }

func (g gate) Wait(context.Context, time.Duration) bool { return g.healthy }

type harness struct {
	svc  *Service
	pub  *fakePublisher
	proc *fakeProcessor
	st   *redisstore.Store
	mr   *miniredis.Miniredis
}

func newHarness(t *testing.T, maxRetries int) *harness {
	t.Helper()
	mr := miniredis.RunT(t)
	st := redisstore.New(mr.Addr(), "")
	pub := &fakePublisher{}
	proc := &fakeProcessor{}
	svc := New(Options{
		WorkerID: "worker-test", Concurrency: 4, ProcessTimeout: time.Second, MaxRetries: maxRetries,
		ReleaseTimeout: time.Minute, ReleaseBatch: 100, StatusTTL: time.Hour,
	}, st, pub, proc, gate{healthy: true}, slog.New(slog.NewTextHandler(io.Discard, nil)))
	return &harness{svc, pub, proc, st, mr}
}

func createdMsg(todoID string) []byte {
	b, _ := json.Marshal(todo.Created{
		EventID: todo.NewID(), EventType: todo.EventTodoCreated, TodoID: todoID, UserID: "user-1", CreatedAt: time.Now().UTC(),
	})
	return b
}

func delivery(body []byte, retry int) rabbitmq.Delivery {
	var h map[string]any
	if retry > 0 {
		h = map[string]any{"x-retry-count": int32(retry)}
	}
	return rabbitmq.NewDelivery(body, "msg-1", h, func(rabbitmq.Outcome) error { return nil })
}

func releaseMsg(scope, todoID string) []byte {
	b, _ := json.Marshal(todo.ReleaseRequested{
		EventID: todo.NewID(), EventType: todo.EventReleaseRequested, Scope: scope, TodoID: todoID,
		RequestedBy: "admin-1", RequestedAt: time.Now().UTC(),
	})
	return b
}

var bg = context.Background()

func equal(a, b []todo.Status) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

// ---- processing ------------------------------------------------------------------------------------

func TestHappyPathReportsProcessingThenWaitingRelease(t *testing.T) {
	h := newHarness(t, 3)

	out := h.svc.HandleCreated(bg, delivery(createdMsg("t1"), 0))

	if out != rabbitmq.Ack {
		t.Fatalf("outcome %v", out)
	}
	if got := h.pub.statuses("t1"); !equal(got, []todo.Status{todo.StatusProcessing, todo.StatusWaitingRelease}) {
		t.Fatalf("reported %v", got)
	}
	rec, ok, _ := h.st.Get(bg, "t1")
	if !ok || rec.Status != todo.StatusWaitingRelease || rec.WorkerID != "worker-test" {
		t.Fatalf("redis record %+v", rec)
	}
	if n, _ := h.st.PendingCount(bg); n != 1 {
		t.Fatalf("pending=%d", n)
	}
}

func TestDuplicateDeliveryIsAcknowledgedWithoutReprocessing(t *testing.T) {
	h := newHarness(t, 3)
	body := createdMsg("t1")

	h.svc.HandleCreated(bg, delivery(body, 0))
	out := h.svc.HandleCreated(bg, delivery(body, 0)) // e.g. the worker crashed before acking the first

	if out != rabbitmq.Ack {
		t.Fatalf("outcome %v", out)
	}
	if h.proc.calls != 1 {
		t.Fatalf("processor ran %d times for the same todo, want 1", h.proc.calls)
	}
	if got := h.pub.statuses("t1"); len(got) != 2 {
		t.Fatalf("duplicate must not publish more events, got %v", got)
	}
}

func TestAMessageForALockedTodoIsHandedBackNotDropped(t *testing.T) {
	h := newHarness(t, 3)
	h.svc.opts.BusyPause = time.Millisecond
	// Another worker took the todo and then crashed: its lock is still there, the broker redelivers its message to us.
	if r, _ := h.st.Begin(bg, "t1", "crashed-worker", "user-1", "evt", time.Now(), 0, time.Minute); r != redisstore.Acquired {
		t.Fatal(r)
	}
	if out := h.svc.HandleCreated(bg, delivery(createdMsg("t1"), 0)); out != rabbitmq.Requeue {
		t.Fatalf("locked todo => %v, want requeue (acking could lose the work)", out)
	}
	if h.proc.calls != 0 {
		t.Fatal("must not process a locked todo")
	}
	// Once the dead worker's lease runs out the same message is processed normally.
	h.mr.FastForward(2 * time.Minute)
	if out := h.svc.HandleCreated(bg, delivery(createdMsg("t1"), 0)); out != rabbitmq.Ack || h.proc.calls != 1 {
		t.Fatalf("takeover => %v, calls=%d", out, h.proc.calls)
	}
}

func TestMalformedMessagesAreDeadLettered(t *testing.T) {
	h := newHarness(t, 3)
	for _, body := range []string{`not json`, `{}`, `{"eventType":"TodoCreated","todoId":"t1"}`, `{"eventType":"Other","todoId":"t","userId":"u"}`} {
		if out := h.svc.HandleCreated(bg, delivery([]byte(body), 0)); out != rabbitmq.Reject {
			t.Fatalf("%q => %v, want reject", body, out)
		}
	}
	if h.proc.calls != 0 {
		t.Fatal("malformed messages must not reach the processor")
	}
}

func TestFailuresAreRetriedThroughTheDelayQueuesThenFailed(t *testing.T) {
	h := newHarness(t, 2)
	h.proc.err = func(int) error { return errors.New("db password is hunter2") } // internal detail must not leak
	body := createdMsg("t1")

	// attempt 0 -> retry tier 1, attempt 1 -> retry tier 2
	for attempt := 0; attempt < 2; attempt++ {
		if out := h.svc.HandleCreated(bg, delivery(body, attempt)); out != rabbitmq.Ack {
			t.Fatalf("attempt %d => %v, want ack (retry copy published)", attempt, out)
		}
		retries := h.pub.byKey(todo.RetryKeyPrefix + string(rune('1'+attempt)))
		if len(retries) != 1 {
			t.Fatalf("attempt %d: expected one publish to retry tier %d", attempt, attempt+1)
		}
		if got := retries[0].P.Headers["x-retry-count"]; got != int32(attempt+1) {
			t.Fatalf("retry header %v", got)
		}
	}
	for _, s := range h.pub.statuses("t1") {
		if s == todo.StatusFailed {
			t.Fatal("must not report FAILED while retries remain")
		}
	}

	// attempt 2 == MaxRetries: out of retries
	out := h.svc.HandleCreated(bg, delivery(body, 2))

	if out != rabbitmq.Reject {
		t.Fatalf("exhausted => %v, want reject (dead-letter)", out)
	}
	last := h.pub.byKey(todo.KeyStatusChanged)
	var evt todo.StatusChanged
	_ = json.Unmarshal(last[len(last)-1].P.Body, &evt)
	if evt.Status != todo.StatusFailed {
		t.Fatalf("last status %s, want FAILED", evt.Status)
	}
	if strings.Contains(evt.Error, "hunter2") || evt.Error != "processing failed" {
		t.Fatalf("error text leaked or unexpected: %q", evt.Error)
	}
	rec, _, _ := h.st.Get(bg, "t1")
	if rec.Status != todo.StatusFailed {
		t.Fatalf("redis status %s", rec.Status)
	}
}

func TestATimeoutCountsAsAFailureWithASafeMessage(t *testing.T) {
	h := newHarness(t, 0)
	h.proc.err = func(int) error { return context.DeadlineExceeded }
	h.svc.HandleCreated(bg, delivery(createdMsg("t1"), 0))
	last := h.pub.byKey(todo.KeyStatusChanged)
	var evt todo.StatusChanged
	_ = json.Unmarshal(last[len(last)-1].P.Body, &evt)
	if evt.Status != todo.StatusFailed || evt.Error != "processing timed out" {
		t.Fatalf("%+v", evt)
	}
}

func TestIfTheRetryCannotBePublishedTheMessageGoesBackToTheBroker(t *testing.T) {
	h := newHarness(t, 3)
	h.proc.err = func(int) error { return errors.New("x") }
	h.pub.fail = func(key string) error {
		if strings.HasPrefix(key, todo.RetryKeyPrefix) {
			return errors.New("broker down")
		}
		return nil
	}
	if out := h.svc.HandleCreated(bg, delivery(createdMsg("t1"), 0)); out != rabbitmq.Requeue {
		t.Fatalf("%v, want requeue so the work is not lost", out)
	}
}

func TestRedisBeingDownLeavesTheMessageWithTheBroker(t *testing.T) {
	h := newHarness(t, 3)
	h.svc.gate = gate{healthy: false}
	if out := h.svc.HandleCreated(bg, delivery(createdMsg("t1"), 0)); out != rabbitmq.Requeue {
		t.Fatalf("gate closed => %v, want requeue", out)
	}
	if h.proc.calls != 0 {
		t.Fatal("must not process while Redis is unavailable")
	}

	// Redis dies after the health probe last looked: Begin() errors, message must not be acked.
	h.svc.gate = gate{healthy: true}
	h.mr.Close()
	if out := h.svc.HandleCreated(bg, delivery(createdMsg("t2"), 0)); out != rabbitmq.Requeue {
		t.Fatalf("redis error => %v, want requeue", out)
	}
	if h.proc.calls != 0 {
		t.Fatal("must not process without the idempotency gate")
	}
}

func TestStatusCannotBeReportedSoTheMessageIsNotAcked(t *testing.T) {
	h := newHarness(t, 3)
	h.pub.fail = func(string) error { return errors.New("broker down") }
	if out := h.svc.HandleCreated(bg, delivery(createdMsg("t1"), 0)); out != rabbitmq.Requeue {
		t.Fatalf("%v", out)
	}
	// and the todo is processable again afterwards (lock released)
	h.pub.fail = nil
	if out := h.svc.HandleCreated(bg, delivery(createdMsg("t1"), 0)); out != rabbitmq.Ack {
		t.Fatalf("second try %v", out)
	}
}

// ---- release ------------------------------------------------------------------------------------------

func waiting(t *testing.T, h *harness, ids ...string) {
	t.Helper()
	for _, id := range ids {
		if out := h.svc.HandleCreated(bg, delivery(createdMsg(id), 0)); out != rabbitmq.Ack {
			t.Fatal(out)
		}
	}
}

func TestManualReleaseOfOneTodoCompletesIt(t *testing.T) {
	h := newHarness(t, 3)
	waiting(t, h, "t1", "t2")

	out := h.svc.HandleRelease(bg, delivery(releaseMsg(todo.ScopeSingle, "t1"), 0))

	if out != rabbitmq.Ack {
		t.Fatal(out)
	}
	got := h.pub.statuses("t1")
	if !equal(got, []todo.Status{todo.StatusProcessing, todo.StatusWaitingRelease, todo.StatusPersisting, todo.StatusCompleted}) {
		t.Fatalf("t1: %v", got)
	}
	if got := h.pub.statuses("t2"); len(got) != 2 {
		t.Fatalf("t2 must be untouched: %v", got)
	}
	rec, _, _ := h.st.Get(bg, "t1")
	if rec.Status != todo.StatusCompleted {
		t.Fatalf("redis: %s", rec.Status)
	}
}

func TestReleasingTwiceIsHarmless(t *testing.T) {
	h := newHarness(t, 3)
	waiting(t, h, "t1")
	cmd := releaseMsg(todo.ScopeSingle, "t1")

	h.svc.HandleRelease(bg, delivery(cmd, 0))
	out := h.svc.HandleRelease(bg, delivery(cmd, 0)) // duplicate command

	if out != rabbitmq.Ack {
		t.Fatal(out)
	}
	completed := 0
	for _, s := range h.pub.statuses("t1") {
		if s == todo.StatusCompleted {
			completed++
		}
	}
	if completed != 1 {
		t.Fatalf("COMPLETED reported %d times", completed)
	}
}

func TestReleaseForATodoThatIsNotPendingIsIgnored(t *testing.T) {
	h := newHarness(t, 3)
	if out := h.svc.HandleRelease(bg, delivery(releaseMsg(todo.ScopeSingle, "unknown"), 0)); out != rabbitmq.Ack {
		t.Fatal(out)
	}
	if len(h.pub.byKey(todo.KeyStatusChanged)) != 0 {
		t.Fatal("nothing should be reported")
	}
}

func TestReleaseAllReleasesEveryPendingTodo(t *testing.T) {
	h := newHarness(t, 3)
	waiting(t, h, "a", "b", "c", "d", "e", "f")

	out := h.svc.HandleRelease(bg, delivery(releaseMsg(todo.ScopeAll, ""), 0))

	if out != rabbitmq.Ack {
		t.Fatal(out)
	}
	for _, id := range []string{"a", "b", "c", "d", "e", "f"} {
		s := h.pub.statuses(id)
		if s[len(s)-1] != todo.StatusCompleted {
			t.Fatalf("%s ended as %v", id, s)
		}
	}
	if n, _ := h.st.PendingCount(bg); n != 0 {
		t.Fatalf("pending=%d", n)
	}
}

func TestMalformedReleaseCommandsAreDeadLettered(t *testing.T) {
	h := newHarness(t, 3)
	for _, body := range []string{`nope`, `{"eventType":"ReleaseRequested","scope":"single"}`, `{"eventType":"ReleaseRequested","scope":"everything"}`, `{"eventType":"TodoCreated","scope":"all"}`} {
		if out := h.svc.HandleRelease(bg, delivery([]byte(body), 0)); out != rabbitmq.Reject {
			t.Fatalf("%q => %v", body, out)
		}
	}
}

func TestAReleaseThatCannotBeReportedIsRecoveredLater(t *testing.T) {
	h := newHarness(t, 3)
	waiting(t, h, "t1")
	h.pub.fail = func(string) error { return errors.New("broker down") }

	h.svc.HandleRelease(bg, delivery(releaseMsg(todo.ScopeSingle, "t1"), 0))

	// Claimed but not finished: parked in-flight, not lost.
	if n, _ := h.st.PendingCount(bg); n != 0 {
		t.Fatalf("pending=%d", n)
	}
	h.svc.now = func() time.Time { return time.Now().Add(10 * time.Minute) }
	n, err := h.svc.RecoverStale(bg)
	if err != nil || n != 1 {
		t.Fatalf("recovered=%d err=%v", n, err)
	}

	h.pub.fail = nil
	h.svc.now = time.Now
	if released, _ := h.svc.ReleasePending(bg); released != 1 {
		t.Fatalf("released=%d after the broker came back", released)
	}
	s := h.pub.statuses("t1")
	if s[len(s)-1] != todo.StatusCompleted {
		t.Fatalf("ended as %v", s)
	}
}

// ---- concurrency ------------------------------------------------------------------------------------

func TestManyJobsAreProcessedConcurrentlyThroughDispatch(t *testing.T) {
	h := newHarness(t, 3)
	var mu sync.Mutex
	running, peak := 0, 0
	h.svc.proc = procFunc(func(ctx context.Context, _ Job) error {
		mu.Lock()
		running++
		peak = max(peak, running)
		mu.Unlock()
		time.Sleep(40 * time.Millisecond)
		mu.Lock()
		running--
		mu.Unlock()
		return nil
	})

	const jobs = 20
	in := make(chan rabbitmq.Delivery, jobs)
	var settled sync.WaitGroup
	settled.Add(jobs)
	for i := 0; i < jobs; i++ {
		id := "todo-" + string(rune('a'+i))
		in <- rabbitmq.NewDelivery(createdMsg(id), id, nil, func(rabbitmq.Outcome) error { settled.Done(); return nil })
	}
	close(in)

	rabbitmq.Dispatch(bg, in, 5, h.svc.HandleCreated, slog.New(slog.NewTextHandler(io.Discard, nil)))
	settled.Wait()

	if peak > 5 || peak < 2 {
		t.Fatalf("peak concurrency %d, want 2..5", peak)
	}
	if n, _ := h.st.PendingCount(bg); n != jobs {
		t.Fatalf("pending=%d, want %d", n, jobs)
	}
}

type procFunc func(context.Context, Job) error

func (f procFunc) Process(ctx context.Context, j Job) error { return f(ctx, j) }
