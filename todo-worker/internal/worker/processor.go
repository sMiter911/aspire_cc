package worker

import (
	"context"
	"math/rand/v2"
	"time"
)

// SimulatedProcessor stands in for real work: it takes Duration and, with probability FailRate, fails.
// It exists to make concurrency, retries and the DLQ observable; replace it with a real Processor.
type SimulatedProcessor struct {
	Duration time.Duration
	FailRate float64
}

type simulatedFailure struct{}

func (simulatedFailure) Error() string  { return "simulated processing failure" }
func (simulatedFailure) Public() string { return "simulated processing failure" }

func (p SimulatedProcessor) Process(ctx context.Context, _ Job) error {
	t := time.NewTimer(p.Duration)
	defer t.Stop()
	select {
	case <-t.C:
	case <-ctx.Done():
		return ctx.Err()
	}
	if p.FailRate > 0 && rand.Float64() < p.FailRate {
		return simulatedFailure{}
	}
	return nil
}
