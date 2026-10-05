package rabbitmq

import (
	"context"
	"io"
	"log/slog"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

var quiet = slog.New(slog.NewTextHandler(io.Discard, nil))

type outcomes struct {
	mu sync.Mutex
	m  map[string]Outcome
}

func (o *outcomes) record(id string) func(Outcome) error {
	return func(out Outcome) error {
		o.mu.Lock()
		defer o.mu.Unlock()
		o.m[id] = out
		return nil
	}
}

func feed(n int, o *outcomes) chan Delivery {
	ch := make(chan Delivery, n)
	for i := 0; i < n; i++ {
		id := string(rune('a' + i))
		ch <- NewDelivery(nil, id, nil, o.record(id))
	}
	close(ch)
	return ch
}

func TestDispatchNeverExceedsTheConcurrencyLimit(t *testing.T) {
	const jobs, limit = 20, 5
	o := &outcomes{m: map[string]Outcome{}}
	var running, peak atomic.Int32

	Dispatch(context.Background(), feed(jobs, o), limit, func(ctx context.Context, d Delivery) Outcome {
		n := running.Add(1)
		for {
			p := peak.Load()
			if n <= p || peak.CompareAndSwap(p, n) {
				break
			}
		}
		time.Sleep(30 * time.Millisecond)
		running.Add(-1)
		return Ack
	}, quiet)

	if got := peak.Load(); got > limit {
		t.Fatalf("peak concurrency %d exceeded the limit %d", got, limit)
	}
	if got := peak.Load(); got < 2 {
		t.Fatalf("jobs did not run concurrently (peak %d)", got)
	}
	if len(o.m) != jobs {
		t.Fatalf("settled %d deliveries, want %d", len(o.m), jobs)
	}
	for id, out := range o.m {
		if out != Ack {
			t.Fatalf("%s settled as %v", id, out)
		}
	}
}

func TestDispatchPassesTheHandlersOutcomeToTheBroker(t *testing.T) {
	o := &outcomes{m: map[string]Outcome{}}
	Dispatch(context.Background(), feed(3, o), 3, func(_ context.Context, d Delivery) Outcome {
		switch d.MessageID {
		case "a":
			return Ack
		case "b":
			return Requeue
		}
		return Reject
	}, quiet)
	if o.m["a"] != Ack || o.m["b"] != Requeue || o.m["c"] != Reject {
		t.Fatalf("unexpected outcomes: %v", o.m)
	}
}

func TestDispatchRequeuesWhatItHasNotStartedWhenStopped(t *testing.T) {
	o := &outcomes{m: map[string]Outcome{}}
	stop, cancel := context.WithCancel(context.Background())
	in := make(chan Delivery, 10)
	for i := 0; i < 10; i++ {
		id := string(rune('a' + i))
		in <- NewDelivery(nil, id, nil, o.record(id))
	}
	started := make(chan struct{})
	release := make(chan struct{})
	var once sync.Once

	done := make(chan struct{})
	go func() {
		Dispatch(stop, in, 1, func(ctx context.Context, d Delivery) Outcome {
			once.Do(func() { close(started) })
			<-release // the in-flight job must be allowed to finish after stop
			return Ack
		}, quiet)
		close(done)
	}()

	<-started
	cancel() // stop while one job runs and nine wait
	close(in)
	time.Sleep(50 * time.Millisecond)
	close(release)
	<-done

	acked, requeued := 0, 0
	for _, out := range o.m {
		switch out {
		case Ack:
			acked++
		case Requeue:
			requeued++
		}
	}
	if acked != 1 || requeued != 9 {
		t.Fatalf("acked=%d requeued=%d, want 1 and 9 (in-flight finishes, the rest go back to the broker)", acked, requeued)
	}
}

func TestDispatchTurnsAPanicIntoARequeue(t *testing.T) {
	o := &outcomes{m: map[string]Outcome{}}
	Dispatch(context.Background(), feed(1, o), 1, func(context.Context, Delivery) Outcome { panic("boom") }, quiet)
	if o.m["a"] != Requeue {
		t.Fatalf("panicking handler settled as %v, want requeue", o.m["a"])
	}
}

func TestRetryCountReadsTheHeader(t *testing.T) {
	for _, v := range []any{int32(2), int64(2), 2} {
		d := Delivery{Headers: map[string]any{"x-retry-count": v}}
		if d.RetryCount() != 2 {
			t.Fatalf("%T not understood", v)
		}
	}
	if (Delivery{}).RetryCount() != 0 {
		t.Fatal("missing header should be 0")
	}
}
