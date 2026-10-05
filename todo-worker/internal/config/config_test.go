package config

import (
	"testing"
	"time"
)

func TestDefaultsAreSane(t *testing.T) {
	for _, k := range []string{"WORKER_CONCURRENCY", "RELEASE_INTERVAL", "MAX_RETRIES"} {
		t.Setenv(k, "")
	}
	c, err := Load()
	if err != nil {
		t.Fatal(err)
	}
	if c.Concurrency != 5 || c.ReleaseInterval != 5*time.Minute || c.MaxRetries != 3 {
		t.Fatalf("%+v", c)
	}
}

func TestEverythingIsConfigurableFromTheEnvironment(t *testing.T) {
	t.Setenv("WORKER_CONCURRENCY", "12")
	t.Setenv("RELEASE_INTERVAL", "30s")
	t.Setenv("AMQP_HOST", "rabbit.internal")
	t.Setenv("REDIS_ADDR", "cache:6380")
	c, err := Load()
	if err != nil {
		t.Fatal(err)
	}
	if c.Concurrency != 12 || c.ReleaseInterval != 30*time.Second || c.AMQPHost != "rabbit.internal" || c.RedisAddr != "cache:6380" {
		t.Fatalf("%+v", c)
	}
}

func TestInvalidValuesAreRejectedAtStartup(t *testing.T) {
	for k, v := range map[string]string{
		"WORKER_CONCURRENCY": "0",
		"MAX_RETRIES":        "9", // only 3 delay queues exist
		"RELEASE_INTERVAL":   "soon",
		"PROCESS_FAIL_RATE":  "2",
	} {
		t.Run(k, func(t *testing.T) {
			t.Setenv(k, v)
			if _, err := Load(); err == nil {
				t.Fatalf("%s=%s accepted", k, v)
			}
		})
	}
}
