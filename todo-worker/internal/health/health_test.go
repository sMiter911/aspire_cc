package health

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

type pinger struct{ err error }

func (p *pinger) Ping(context.Context) error { return p.err }

type broker struct{ up bool }

func (b broker) Connected() bool { return b.up }

func get(t *testing.T, h http.Handler, path string) (int, string) {
	t.Helper()
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, path, nil))
	return rec.Code, rec.Body.String()
}

func TestLivenessIsAlwaysOK(t *testing.T) {
	m := NewMonitor(&pinger{err: errors.New("down")}, broker{up: false})
	if code, _ := get(t, m.Handler(), "/health"); code != http.StatusOK {
		t.Fatalf("/health = %d", code)
	}
}

func TestReadinessNeedsBothRabbitMQAndRedis(t *testing.T) {
	cases := []struct {
		name  string
		redis error
		up    bool
		want  int
	}{
		{"both up", nil, true, 200},
		{"redis down", errors.New("x"), true, 503},
		{"rabbitmq down", nil, false, 503},
		{"both down", errors.New("x"), false, 503},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			m := NewMonitor(&pinger{err: c.redis}, broker{up: c.up})
			code, body := get(t, m.Handler(), "/ready")
			if code != c.want {
				t.Fatalf("/ready = %d, want %d", code, c.want)
			}
			// no connection details, hostnames or error text in the (anonymous) response
			if strings.Contains(body, "localhost") || strings.Contains(body, "x") && c.redis != nil && strings.Contains(body, c.redis.Error()+"\"") {
				t.Fatalf("response leaks details: %s", body)
			}
		})
	}
}

func TestWaitReturnsWhenRedisRecoversAndGivesUpOtherwise(t *testing.T) {
	p := &pinger{err: errors.New("down")}
	m := NewMonitor(p, broker{up: true})
	m.probe(context.Background())
	if m.Wait(context.Background(), 300*time.Millisecond) {
		t.Fatal("should give up while redis is down")
	}
	p.err = nil
	go func() { time.Sleep(200 * time.Millisecond); m.probe(context.Background()) }()
	if !m.Wait(context.Background(), 3*time.Second) {
		t.Fatal("should return once redis is back")
	}
}
