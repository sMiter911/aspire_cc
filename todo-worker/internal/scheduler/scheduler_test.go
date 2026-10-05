package scheduler

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"sync/atomic"
	"testing"
	"time"
)

type fake struct {
	releases, recovers atomic.Int32
	releaseErr         error
}

func (f *fake) ReleasePending(context.Context) (int, error) {
	f.releases.Add(1)
	return 1, f.releaseErr
}
func (f *fake) RecoverStale(context.Context) (int, error) { f.recovers.Add(1); return 0, nil }

var quiet = slog.New(slog.NewTextHandler(io.Discard, nil))

func TestRunTicksOnTheConfiguredInterval(t *testing.T) {
	f := &fake{}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() { Run(ctx, 20*time.Millisecond, f, quiet); close(done) }()

	time.Sleep(110 * time.Millisecond)
	cancel()
	<-done

	if n := f.releases.Load(); n < 3 || n > 6 {
		t.Fatalf("released %d times in ~110ms at a 20ms interval", n)
	}
	if f.recovers.Load() != f.releases.Load() {
		t.Fatal("every tick should also recover stale releases")
	}
}

func TestTheFirstTickWaitsAFullInterval(t *testing.T) {
	f := &fake{}
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	Run(ctx, time.Hour, f, quiet)
	if f.releases.Load() != 0 {
		t.Fatal("scheduler must not fire before the first interval elapses")
	}
}

func TestErrorsDoNotStopTheScheduler(t *testing.T) {
	f := &fake{releaseErr: errors.New("redis down")}
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	Run(ctx, 15*time.Millisecond, f, quiet)
	if f.releases.Load() < 3 {
		t.Fatalf("scheduler gave up after errors (%d ticks)", f.releases.Load())
	}
}
