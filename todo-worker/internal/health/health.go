// Package health exposes /health (liveness) and /ready (can the worker actually do its job?) and tracks Redis
// availability so job handlers can wait for it instead of spinning.
package health

import (
	"context"
	"encoding/json"
	"log/slog"
	"net/http"
	"sync/atomic"
	"time"
)

// Pinger is anything that can be health checked (the Redis store).
type Pinger interface {
	Ping(ctx context.Context) error
}

// Connected reports broker connectivity.
type Connected interface{ Connected() bool }

type Monitor struct {
	redis        Pinger
	broker       Connected
	redisHealthy atomic.Bool
	probed       atomic.Bool
	log          *slog.Logger
}

// NewMonitor takes an optional logger; state changes (healthy <-> unhealthy) are logged once, not on every probe.
func NewMonitor(redis Pinger, broker Connected, log ...*slog.Logger) *Monitor {
	m := &Monitor{redis: redis, broker: broker}
	if len(log) > 0 {
		m.log = log[0]
	}
	return m
}

// Run probes Redis periodically until ctx ends.
func (m *Monitor) Run(ctx context.Context, every time.Duration) {
	m.probe(ctx)
	t := time.NewTicker(every)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			m.probe(ctx)
		}
	}
}

func (m *Monitor) probe(ctx context.Context) {
	pctx, cancel := context.WithTimeout(ctx, time.Second)
	defer cancel()
	err := m.redis.Ping(pctx)
	ok := err == nil
	was := m.redisHealthy.Swap(ok)
	first := !m.probed.Swap(true)
	if m.log != nil && (ok != was || first) {
		if ok {
			m.log.Info("redis is available")
		} else {
			m.log.Warn("redis is unavailable", "error", err)
		}
	}
}

// RedisHealthy is the last probe result.
func (m *Monitor) RedisHealthy() bool { return m.redisHealthy.Load() }

// Wait implements worker.Gate.
func (m *Monitor) Wait(ctx context.Context, max time.Duration) bool {
	if m.RedisHealthy() {
		return true
	}
	deadline := time.NewTimer(max)
	defer deadline.Stop()
	tick := time.NewTicker(250 * time.Millisecond)
	defer tick.Stop()
	for {
		select {
		case <-ctx.Done():
			return false
		case <-deadline.C:
			return m.RedisHealthy()
		case <-tick.C:
			if m.RedisHealthy() {
				return true
			}
		}
	}
}

// Handler serves /health and /ready. Responses carry only ok/not-ready, never connection details.
func (m *Monitor) Handler() http.Handler {
	mux := http.NewServeMux()
	write := func(w http.ResponseWriter, code int, status string) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(code)
		_ = json.NewEncoder(w).Encode(map[string]string{"status": status})
	}
	mux.HandleFunc("GET /health", func(w http.ResponseWriter, _ *http.Request) { write(w, http.StatusOK, "ok") })
	mux.HandleFunc("GET /ready", func(w http.ResponseWriter, r *http.Request) {
		ctx, cancel := context.WithTimeout(r.Context(), time.Second)
		defer cancel()
		if m.broker.Connected() && m.redis.Ping(ctx) == nil {
			write(w, http.StatusOK, "ready")
			return
		}
		write(w, http.StatusServiceUnavailable, "not ready")
	})
	return mux
}
