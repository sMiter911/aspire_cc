// Package scheduler runs the periodic release: every RELEASE_INTERVAL it recovers stale in-flight releases and
// releases everything waiting in Redis. Several workers can run it at once: claims in Redis are atomic.
package scheduler

import (
	"context"
	"log/slog"
	"time"
)

// Releaser is implemented by worker.Service.
type Releaser interface {
	ReleasePending(ctx context.Context) (int, error)
	RecoverStale(ctx context.Context) (int, error)
}

// Run blocks until ctx is cancelled, ticking every interval. The first tick happens after one full interval.
func Run(ctx context.Context, interval time.Duration, r Releaser, log *slog.Logger) {
	t := time.NewTicker(interval)
	defer t.Stop()
	log.Info("release scheduler started", "interval", interval.String())
	for {
		select {
		case <-ctx.Done():
			log.Info("release scheduler stopped")
			return
		case <-t.C:
			Tick(ctx, r, log)
		}
	}
}

// Tick performs one scheduler pass. Errors are logged and never stop the scheduler (Redis may be down for a
// while; the next tick tries again).
func Tick(ctx context.Context, r Releaser, log *slog.Logger) {
	if n, err := r.RecoverStale(ctx); err != nil {
		log.Warn("stale release recovery failed", "error", err)
	} else if n > 0 {
		log.Warn("recovered stale releases", "count", n)
	}
	n, err := r.ReleasePending(ctx)
	if err != nil {
		log.Warn("scheduled release failed", "error", err)
		return
	}
	if n > 0 {
		log.Info("scheduled release complete", "released", n)
	}
}
