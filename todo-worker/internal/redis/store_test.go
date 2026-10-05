package redis

import (
	"context"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/example/todo-worker/internal/todo"
)

func newStore(t *testing.T) (*Store, *miniredis.Miniredis) {
	t.Helper()
	mr := miniredis.RunT(t)
	return New(mr.Addr(), ""), mr
}

var ctx = context.Background()

func begin(t *testing.T, s *Store, id, worker string) BeginResult {
	t.Helper()
	r, err := s.Begin(ctx, id, worker, "user-1", "evt-1", time.Now(), 0, time.Minute)
	if err != nil {
		t.Fatal(err)
	}
	return r
}

func TestBeginIsAnAtomicIdempotencyGate(t *testing.T) {
	s, _ := newStore(t)

	if got := begin(t, s, "t1", "w1"); got != Acquired {
		t.Fatalf("first begin = %s", got)
	}
	// A second worker receiving the same todo while the first is working must not process it too.
	if got := begin(t, s, "t1", "w2"); got != Busy {
		t.Fatalf("concurrent begin = %s, want BUSY", got)
	}
	rec, ok, _ := s.Get(ctx, "t1")
	if !ok || rec.Status != todo.StatusProcessing || rec.WorkerID != "w1" {
		t.Fatalf("unexpected record %+v", rec)
	}

	if err := s.MarkWaitingRelease(ctx, "t1", "w1", time.Now()); err != nil {
		t.Fatal(err)
	}
	// Redelivery after a crash-before-ack: already waiting for release, so it is a duplicate.
	if got := begin(t, s, "t1", "w2"); got != Duplicate {
		t.Fatalf("redelivery = %s, want DUPLICATE", got)
	}
}

func TestAFailedAttemptCanBeRetried(t *testing.T) {
	s, _ := newStore(t)
	begin(t, s, "t1", "w1")
	if err := s.Abandon(ctx, "t1", "processing failed"); err != nil {
		t.Fatal(err)
	}
	if got := begin(t, s, "t1", "w1"); got != Acquired {
		t.Fatalf("retry after abandon = %s", got)
	}
	// A permanently failed todo may be re-queued by an admin: FAILED is not a duplicate.
	_ = s.MarkFailed(ctx, "t1", "w1", "x", time.Hour)
	if got := begin(t, s, "t1", "w1"); got != Acquired {
		t.Fatalf("begin after FAILED = %s, want ACQUIRED (admin retry)", got)
	}
}

func TestALostLockExpiresSoAnotherWorkerCanTakeOver(t *testing.T) {
	s, mr := newStore(t)
	if r, _ := s.Begin(ctx, "t1", "w1", "u", "e", time.Now(), 0, 50*time.Millisecond); r != Acquired {
		t.Fatal(r)
	}
	mr.FastForward(time.Second) // the crashed worker's lease runs out
	if got := begin(t, s, "t1", "w2"); got != Acquired {
		t.Fatalf("takeover = %s", got)
	}
}

func TestOnlyOneCallerCanClaimAPendingTodo(t *testing.T) {
	s, _ := newStore(t)
	begin(t, s, "t1", "w1")
	_ = s.MarkWaitingRelease(ctx, "t1", "w1", time.Now())

	var wins atomic.Int32
	var wg sync.WaitGroup
	for i := 0; i < 20; i++ { // manual release and the timer racing each other
		wg.Add(1)
		go func() {
			defer wg.Done()
			ok, err := s.ClaimOne(ctx, "t1", time.Now())
			if err != nil {
				t.Error(err)
			}
			if ok {
				wins.Add(1)
			}
		}()
	}
	wg.Wait()
	if wins.Load() != 1 {
		t.Fatalf("%d callers claimed the same todo, want exactly 1", wins.Load())
	}
}

func TestClaimPendingTakesOldestFirstAndHonoursTheLimit(t *testing.T) {
	s, _ := newStore(t)
	base := time.Now()
	for i, id := range []string{"a", "b", "c"} {
		begin(t, s, id, "w")
		_ = s.MarkWaitingRelease(ctx, id, "w", base.Add(time.Duration(i)*time.Second))
	}
	ids, err := s.ClaimPending(ctx, 2, time.Now())
	if err != nil || len(ids) != 2 || ids[0] != "a" || ids[1] != "b" {
		t.Fatalf("ids=%v err=%v", ids, err)
	}
	if n, _ := s.PendingCount(ctx); n != 1 {
		t.Fatalf("pending=%d, want 1", n)
	}
}

func TestStaleInflightReleasesGoBackToPending(t *testing.T) {
	s, _ := newStore(t)
	begin(t, s, "t1", "w")
	_ = s.MarkWaitingRelease(ctx, "t1", "w", time.Now())
	claimedAt := time.Now().Add(-10 * time.Minute)
	if ok, _ := s.ClaimOne(ctx, "t1", claimedAt); !ok {
		t.Fatal("claim failed")
	}
	// The releasing worker crashed: nothing finished the release.
	n, err := s.RecoverStale(ctx, time.Now(), 2*time.Minute)
	if err != nil || n != 1 {
		t.Fatalf("recovered=%d err=%v", n, err)
	}
	if c, _ := s.PendingCount(ctx); c != 1 {
		t.Fatalf("pending=%d after recovery", c)
	}
}

func TestFinishedReleasesAreNotRecovered(t *testing.T) {
	s, _ := newStore(t)
	begin(t, s, "t1", "w")
	_ = s.MarkWaitingRelease(ctx, "t1", "w", time.Now())
	_, _ = s.ClaimOne(ctx, "t1", time.Now().Add(-time.Hour))
	_ = s.Finish(ctx, "t1")
	if n, _ := s.RecoverStale(ctx, time.Now(), time.Minute); n != 0 {
		t.Fatalf("recovered %d finished releases", n)
	}
}

func TestTerminalStatusesExpire(t *testing.T) {
	s, mr := newStore(t)
	begin(t, s, "t1", "w")
	_ = s.SetStatus(ctx, "t1", todo.StatusCompleted, "w", time.Hour)
	mr.FastForward(2 * time.Hour)
	if _, ok, _ := s.Get(ctx, "t1"); ok {
		t.Fatal("completed status should have expired")
	}
}
