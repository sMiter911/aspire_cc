// Command worker consumes TodoCreated messages from RabbitMQ, processes them concurrently, tracks state in Redis,
// and reports status back to the Todo API through RabbitMQ.
package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"

	"github.com/example/todo-worker/internal/config"
	"github.com/example/todo-worker/internal/health"
	"github.com/example/todo-worker/internal/rabbitmq"
	redisstore "github.com/example/todo-worker/internal/redis"
	"github.com/example/todo-worker/internal/scheduler"
	"github.com/example/todo-worker/internal/todo"
	"github.com/example/todo-worker/internal/worker"
)

func main() {
	cfg, err := config.Load()
	if err != nil {
		fmt.Fprintln(os.Stderr, "invalid configuration:", err)
		os.Exit(2)
	}
	level := slog.LevelInfo
	_ = level.UnmarshalText([]byte(cfg.LogLevel))
	log := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: level})).With("service", "todo-worker")

	if err := run(cfg, log); err != nil {
		log.Error("worker stopped with error", "error", err)
		os.Exit(1)
	}
}

func run(cfg config.Config, log *slog.Logger) error {
	// SIGINT/SIGTERM cancel ctx: stop taking new messages, let in-flight jobs finish, then exit.
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	store := redisstore.New(cfg.RedisAddr, cfg.RedisPassword)
	defer store.Close()

	broker := rabbitmq.NewClient(rabbitmq.Config{
		Host: cfg.AMQPHost, Port: cfg.AMQPPort, User: cfg.AMQPUser, Password: cfg.AMQPPassword,
		VHost: cfg.AMQPVHost, ConnectionName: cfg.WorkerID,
	}, log)

	monitor := health.NewMonitor(store, broker, log)
	svc := worker.New(worker.Options{
		WorkerID: cfg.WorkerID, Concurrency: cfg.Concurrency, ProcessTimeout: cfg.ProcessTimeout,
		MaxRetries: cfg.MaxRetries, ReleaseTimeout: cfg.ReleaseTimeout, ReleaseBatch: cfg.ReleaseBatch,
		StatusTTL: cfg.StatusTTL,
	}, store, broker, worker.SimulatedProcessor{Duration: cfg.ProcessDuration, FailRate: cfg.FailRate}, monitor, log)

	log.Info("worker starting",
		"worker_id", cfg.WorkerID, "concurrency", cfg.Concurrency, "max_retries", cfg.MaxRetries,
		"release_interval", cfg.ReleaseInterval.String(), "process_duration", cfg.ProcessDuration.String())

	var wg sync.WaitGroup
	start := func(f func()) { wg.Add(1); go func() { defer wg.Done(); f() }() }

	start(func() { broker.Run(ctx) })
	start(func() { monitor.Run(ctx, 2*time.Second) })
	start(func() { scheduler.Run(ctx, cfg.ReleaseInterval, svc, log) })
	// Processing queue: up to Concurrency jobs at once (prefetch == concurrency).
	start(func() { _ = broker.Consume(ctx, todo.QueueProcessing, cfg.Concurrency, svc.HandleCreated) })
	// Admin release commands are rare and cheap; a small prefetch keeps them from starving processing.
	start(func() { _ = broker.Consume(ctx, todo.QueueRelease, 2, svc.HandleRelease) })

	srv := &http.Server{Addr: net.JoinHostPort("", cfg.HTTPPort), Handler: monitor.Handler(), ReadHeaderTimeout: 5 * time.Second}
	start(func() {
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Error("health server failed", "error", err)
			stop()
		}
	})

	<-ctx.Done()
	log.Info("shutdown requested: draining in-flight jobs")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), cfg.ProcessTimeout+10*time.Second)
	defer cancel()
	_ = srv.Shutdown(shutdownCtx)

	done := make(chan struct{})
	go func() { wg.Wait(); close(done) }()
	select {
	case <-done:
		log.Info("worker stopped cleanly")
	case <-shutdownCtx.Done():
		log.Warn("shutdown timed out; unacknowledged messages will be redelivered by RabbitMQ")
	}
	return nil
}
