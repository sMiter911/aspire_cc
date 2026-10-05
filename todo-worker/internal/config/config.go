// Package config reads all settings from environment variables. Nothing infrastructure-related has a baked-in value
// except local-development fallbacks; Aspire injects the real endpoints and credentials.
package config

import (
	"fmt"
	"os"
	"strconv"
	"time"

	"github.com/example/todo-worker/internal/todo"
)

type Config struct {
	WorkerID string

	AMQPHost     string
	AMQPPort     int
	AMQPUser     string
	AMQPPassword string
	AMQPVHost    string

	RedisAddr     string
	RedisPassword string

	// Concurrency is the hard cap on jobs processed at once; it is also the consumer prefetch.
	Concurrency    int
	ProcessTimeout time.Duration
	// MaxRetries is the number of re-deliveries via the delay tiers before a job is failed and dead-lettered.
	// Backoff delays per tier (5s, 30s, 2m) are queue TTLs in messaging/rabbitmq/definitions.json.
	MaxRetries int

	ReleaseInterval time.Duration
	// ReleaseTimeout: a claimed release that has not finished after this long is put back in the pending set.
	ReleaseTimeout time.Duration
	ReleaseBatch   int

	// StatusTTL bounds how long terminal job records stay in Redis.
	StatusTTL time.Duration

	// Simulated work (the placeholder for a real processing step).
	ProcessDuration time.Duration
	FailRate        float64

	HTTPPort string
	LogLevel string
}

// Load reads and validates the configuration.
func Load() (Config, error) {
	host, _ := os.Hostname()
	c := Config{
		WorkerID:        env("WORKER_ID", fmt.Sprintf("worker-%s-%d", host, os.Getpid())),
		AMQPHost:        env("AMQP_HOST", "localhost"),
		AMQPUser:        env("AMQP_USER", "guest"),
		AMQPPassword:    env("AMQP_PASSWORD", "guest"),
		AMQPVHost:       env("AMQP_VHOST", "/"),
		RedisAddr:       env("REDIS_ADDR", "localhost:6379"),
		RedisPassword:   env("REDIS_PASSWORD", ""),
		HTTPPort:        env("PORT", "8081"),
		LogLevel:        env("LOG_LEVEL", "info"),
		ProcessDuration: 3 * time.Second,
	}
	var err error
	if c.AMQPPort, err = envInt("AMQP_PORT", 5672); err != nil {
		return c, err
	}
	if c.Concurrency, err = envInt("WORKER_CONCURRENCY", 5); err != nil {
		return c, err
	}
	if c.MaxRetries, err = envInt("MAX_RETRIES", 3); err != nil {
		return c, err
	}
	if c.ReleaseBatch, err = envInt("RELEASE_BATCH", 100); err != nil {
		return c, err
	}
	if c.ProcessTimeout, err = envDuration("PROCESS_TIMEOUT", 30*time.Second); err != nil {
		return c, err
	}
	if c.ReleaseInterval, err = envDuration("RELEASE_INTERVAL", 5*time.Minute); err != nil {
		return c, err
	}
	if c.ReleaseTimeout, err = envDuration("RELEASE_TIMEOUT", 2*time.Minute); err != nil {
		return c, err
	}
	if c.StatusTTL, err = envDuration("STATUS_TTL", 24*time.Hour); err != nil {
		return c, err
	}
	if c.ProcessDuration, err = envDuration("PROCESS_DURATION", c.ProcessDuration); err != nil {
		return c, err
	}
	if v := os.Getenv("PROCESS_FAIL_RATE"); v != "" {
		if c.FailRate, err = strconv.ParseFloat(v, 64); err != nil {
			return c, fmt.Errorf("PROCESS_FAIL_RATE: %w", err)
		}
	}
	return c, c.Validate()
}

// Validate rejects settings that would make the worker unsafe or pointless.
func (c Config) Validate() error {
	switch {
	case c.Concurrency < 1:
		return fmt.Errorf("WORKER_CONCURRENCY must be >= 1, got %d", c.Concurrency)
	case c.MaxRetries < 0 || c.MaxRetries > todo.MaxRetryTiers:
		return fmt.Errorf("MAX_RETRIES must be 0..%d (one delay queue per retry), got %d", todo.MaxRetryTiers, c.MaxRetries)
	case c.ReleaseInterval <= 0:
		return fmt.Errorf("RELEASE_INTERVAL must be positive")
	case c.ProcessTimeout <= 0:
		return fmt.Errorf("PROCESS_TIMEOUT must be positive")
	case c.FailRate < 0 || c.FailRate > 1:
		return fmt.Errorf("PROCESS_FAIL_RATE must be within 0..1")
	}
	return nil
}

func env(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func envInt(key string, def int) (int, error) {
	v := os.Getenv(key)
	if v == "" {
		return def, nil
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		return 0, fmt.Errorf("%s: %w", key, err)
	}
	return n, nil
}

func envDuration(key string, def time.Duration) (time.Duration, error) {
	v := os.Getenv(key)
	if v == "" {
		return def, nil
	}
	d, err := time.ParseDuration(v)
	if err != nil {
		return 0, fmt.Errorf("%s: %w", key, err)
	}
	return d, nil
}
