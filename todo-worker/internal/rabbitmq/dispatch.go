package rabbitmq

import (
	"context"
	"log/slog"
	"sync"
)

// Outcome tells the broker what to do with a delivery.
type Outcome int

const (
	// Ack: processed (or safely ignorable); remove from the queue.
	Ack Outcome = iota
	// Requeue: could not process now (dependency down); give it back to the broker untouched.
	Requeue
	// Reject: cannot ever succeed; dead-letter it (the queue's DLX routes it to the DLQ).
	Reject
)

func (o Outcome) String() string {
	switch o {
	case Ack:
		return "ack"
	case Requeue:
		return "requeue"
	default:
		return "reject"
	}
}

// Delivery is a broker-agnostic view of one message plus the means to settle it.
type Delivery struct {
	Body        []byte
	MessageID   string
	Type        string
	RoutingKey  string
	Headers     map[string]any
	Redelivered bool

	settle func(Outcome) error
}

// NewDelivery builds a Delivery with a custom settle function (used by tests and the AMQP adapter).
func NewDelivery(body []byte, messageID string, headers map[string]any, settle func(Outcome) error) Delivery {
	return Delivery{Body: body, MessageID: messageID, Headers: headers, settle: settle}
}

// Settle acknowledges the delivery with the given outcome.
func (d Delivery) Settle(o Outcome) error { return d.settle(o) }

// RetryCount returns the x-retry-count header (0 when absent).
func (d Delivery) RetryCount() int {
	switch v := d.Headers["x-retry-count"].(type) {
	case int:
		return v
	case int32:
		return int(v)
	case int64:
		return int(v)
	}
	return 0
}

// Handler processes one delivery and says how to settle it.
type Handler func(ctx context.Context, d Delivery) Outcome

// Pool bounds how many handlers run at once. It outlives individual AMQP channels: after a reconnect the new
// consumer shares the same slots, so the limit counts goroutines that really are still running (a job stuck on a
// dead channel keeps its slot until it finishes or times out).
type Pool struct {
	sem chan struct{}
	wg  sync.WaitGroup
}

func NewPool(limit int) *Pool { return &Pool{sem: make(chan struct{}, limit)} }

// Wait blocks until every started handler has finished (used for graceful shutdown).
func (p *Pool) Wait() { p.wg.Wait() }

// Dispatch is DispatchPool with a private pool, waiting for the handlers to finish.
func Dispatch(stop context.Context, in <-chan Delivery, limit int, h Handler, log *slog.Logger) {
	p := NewPool(limit)
	DispatchPool(stop, in, p, h, log)
	p.Wait()
}

// DispatchPool reads deliveries and runs the handler for each in its own goroutine, never more than the pool's
// limit at once. It returns when `in` is closed, WITHOUT waiting for running handlers.
//
// Concurrency model: a buffered channel is a counting semaphore. The reader blocks on acquiring a slot, so at most
// `limit` goroutines exist and the unread deliveries wait in the AMQP client/broker (backpressure), not in memory.
//
// Shutdown model: when stop is cancelled no new handler is started; deliveries that were already pushed to us are
// returned to the broker (Requeue) and in-flight handlers are allowed to finish (see Pool.Wait). Handlers therefore
// receive a context that is NOT cancelled by stop; each handler enforces its own timeout.
func DispatchPool(stop context.Context, in <-chan Delivery, pool *Pool, h Handler, log *slog.Logger) {
	jobCtx := context.WithoutCancel(stop)

	settle := func(d Delivery, o Outcome) {
		if err := d.Settle(o); err != nil {
			// The channel closed underneath us: the broker will redeliver. Safe because handlers are idempotent.
			log.Warn("could not settle delivery", "message_id", d.MessageID, "outcome", o.String(), "error", err)
		}
	}

	for d := range in {
		if stop.Err() != nil {
			settle(d, Requeue)
			continue // keep draining until the consumer is cancelled and the channel closes
		}
		select {
		case pool.sem <- struct{}{}:
		case <-stop.Done():
			settle(d, Requeue)
			continue
		}
		pool.wg.Add(1)
		go func(d Delivery) {
			defer func() { <-pool.sem; pool.wg.Done() }()
			defer func() {
				if r := recover(); r != nil { // a panicking job must not kill the worker or lose the message
					log.Error("handler panic", "message_id", d.MessageID, "panic", r)
					settle(d, Requeue)
				}
			}()
			settle(d, h(jobCtx, d))
		}(d)
	}
}
