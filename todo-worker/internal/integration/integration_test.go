// Package integration runs the real worker against a real RabbitMQ and Redis (Testcontainers), using the same
// definitions.json the Aspire broker loads. Skipped with -short; without Docker they fail rather than pass silently.
package integration

import (
	"bytes"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/example/todo-worker/internal/health"
	"github.com/example/todo-worker/internal/rabbitmq"
	redisstore "github.com/example/todo-worker/internal/redis"
	"github.com/example/todo-worker/internal/scheduler"
	"github.com/example/todo-worker/internal/todo"
	"github.com/example/todo-worker/internal/worker"
	amqp "github.com/rabbitmq/amqp091-go"
	goredis "github.com/redis/go-redis/v9"
	"github.com/testcontainers/testcontainers-go"
	tcrabbit "github.com/testcontainers/testcontainers-go/modules/rabbitmq"
	tcredis "github.com/testcontainers/testcontainers-go/modules/redis"
)

const retryDelay = 300 // ms: the production tiers (5s/30s/2m) are shortened so the tests stay fast

type infra struct {
	rabbit    *tcrabbit.RabbitMQContainer
	amqpURL   string
	host      string
	port      int
	redisAddr string
	status    *statusCollector
}

var shared *infra

func TestMain(m *testing.M) {
	flag.Parse()
	if testing.Short() {
		os.Exit(0)
	}
	ctx := context.Background()
	in, cleanup, err := start(ctx)
	if err != nil {
		// Never pass silently: use -short to skip these on machines without Docker.
		fmt.Println("FAIL integration tests cannot start containers (use -short to skip):", err)
		os.Exit(1)
	}
	shared = in
	code := m.Run()
	cleanup()
	os.Exit(code)
}

func start(ctx context.Context) (*infra, func(), error) {
	rc, err := tcrabbit.Run(ctx, "rabbitmq:4-management-alpine")
	if err != nil {
		return nil, nil, err
	}
	redisC, err := tcredis.Run(ctx, "redis:7-alpine")
	if err != nil {
		_ = testcontainers.TerminateContainer(rc)
		return nil, nil, err
	}
	amqpURL, _ := rc.AmqpURL(ctx)
	u, _ := url.Parse(amqpURL)
	port, _ := strconv.Atoi(u.Port())
	httpURL, _ := rc.HttpURL(ctx)
	rs, _ := redisC.ConnectionString(ctx)
	ru, _ := url.Parse(rs)

	if err := importDefinitions(httpURL); err != nil {
		return nil, nil, err
	}
	in := &infra{rabbit: rc, amqpURL: amqpURL, host: u.Hostname(), port: port, redisAddr: ru.Host}
	in.status = newStatusCollector(amqpURL)
	return in, func() {
		in.status.stop()
		_ = testcontainers.TerminateContainer(rc)
		_ = testcontainers.TerminateContainer(redisC)
	}, nil
}

// importDefinitions loads the REAL topology file, shortening only the retry-tier TTLs.
func importDefinitions(httpURL string) error {
	raw, err := os.ReadFile(filepath.Join("..", "..", "..", "messaging", "rabbitmq", "definitions.json"))
	if err != nil {
		return err
	}
	var def map[string]any
	if err := json.Unmarshal(raw, &def); err != nil {
		return err
	}
	for _, q := range def["queues"].([]any) {
		qm := q.(map[string]any)
		if args := qm["arguments"].(map[string]any); args["x-message-ttl"] != nil {
			args["x-message-ttl"] = retryDelay
		}
	}
	body, _ := json.Marshal(def)
	var last error
	for range 60 { // the management plugin needs a few seconds after the broker accepts AMQP
		req, _ := http.NewRequest(http.MethodPost, httpURL+"/api/definitions", bytes.NewReader(body))
		req.SetBasicAuth("guest", "guest")
		req.Header.Set("Content-Type", "application/json")
		resp, err := http.DefaultClient.Do(req)
		if err == nil {
			b, _ := io.ReadAll(resp.Body)
			resp.Body.Close()
			if resp.StatusCode < 300 {
				return nil
			}
			last = fmt.Errorf("import definitions: %s %s", resp.Status, b)
		} else {
			last = err
		}
		time.Sleep(time.Second)
	}
	return last
}

// ---- status collector: plays the Todo API's role on the todo.status queue ------------------------------

type statusCollector struct {
	mu     sync.Mutex
	events map[string][]todo.Status
	cancel context.CancelFunc
}

func newStatusCollector(url string) *statusCollector {
	ctx, cancel := context.WithCancel(context.Background())
	c := &statusCollector{events: map[string][]todo.Status{}, cancel: cancel}
	go func() {
		for ctx.Err() == nil {
			c.run(ctx, url)
			time.Sleep(200 * time.Millisecond)
		}
	}()
	return c
}

func (c *statusCollector) run(ctx context.Context, url string) {
	conn, err := amqp.Dial(url)
	if err != nil {
		return
	}
	defer conn.Close()
	ch, err := conn.Channel()
	if err != nil {
		return
	}
	msgs, err := ch.Consume("todo.status", "", false, false, false, false, nil)
	if err != nil {
		return
	}
	closed := conn.NotifyClose(make(chan *amqp.Error, 1))
	for {
		select {
		case <-ctx.Done():
			return
		case <-closed:
			return
		case m, ok := <-msgs:
			if !ok {
				return
			}
			var e todo.StatusChanged
			if json.Unmarshal(m.Body, &e) == nil {
				c.mu.Lock()
				c.events[e.TodoID] = append(c.events[e.TodoID], e.Status)
				c.mu.Unlock()
			}
			_ = m.Ack(false)
		}
	}
}

func (c *statusCollector) stop() { c.cancel() }

func (c *statusCollector) get(id string) []todo.Status {
	c.mu.Lock()
	defer c.mu.Unlock()
	return append([]todo.Status(nil), c.events[id]...)
}

func (c *statusCollector) waitFor(t *testing.T, id string, want todo.Status, timeout time.Duration) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		for _, s := range c.get(id) {
			if s == want {
				return
			}
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatalf("todo %s never reported %s (saw %v)", id, want, c.get(id))
}

func count(list []todo.Status, s todo.Status) int {
	n := 0
	for _, x := range list {
		if x == s {
			n++
		}
	}
	return n
}

// ---- worker harness ---------------------------------------------------------------------------------

type workerOpts struct {
	id              string
	concurrency     int
	maxRetries      int
	processTimeout  time.Duration
	releaseInterval time.Duration // 0 = no scheduler
	proc            worker.Processor
}

type runningWorker struct {
	client *rabbitmq.Client
	stop   func()
}

func startWorker(t *testing.T, in *infra, o workerOpts) *runningWorker {
	t.Helper()
	log := slog.New(slog.NewTextHandler(io.Discard, nil))
	if o.concurrency == 0 {
		o.concurrency = 5
	}
	if o.processTimeout == 0 {
		o.processTimeout = 5 * time.Second
	}
	store := redisstore.New(in.redisAddr, "")
	client := rabbitmq.NewClient(rabbitmq.Config{Host: in.host, Port: in.port, User: "guest", Password: "guest", VHost: "/", ConnectionName: o.id}, log)
	mon := health.NewMonitor(store, client)
	svc := worker.New(worker.Options{
		WorkerID: o.id, Concurrency: o.concurrency, ProcessTimeout: o.processTimeout, MaxRetries: o.maxRetries,
		ReleaseTimeout: time.Minute, ReleaseBatch: 100, StatusTTL: time.Hour, DependencyWait: 20 * time.Second,
		BusyPause: 300 * time.Millisecond,
	}, store, client, o.proc, mon, log)

	ctx, cancel := context.WithCancel(context.Background())
	var wg sync.WaitGroup
	run := func(f func()) { wg.Add(1); go func() { defer wg.Done(); f() }() }
	run(func() { client.Run(ctx) })
	run(func() { mon.Run(ctx, 500*time.Millisecond) })
	run(func() { _ = client.Consume(ctx, todo.QueueProcessing, o.concurrency, svc.HandleCreated) })
	run(func() { _ = client.Consume(ctx, todo.QueueRelease, 2, svc.HandleRelease) })
	if o.releaseInterval > 0 {
		run(func() { scheduler.Run(ctx, o.releaseInterval, svc, log) })
	}
	rw := &runningWorker{client: client, stop: func() { cancel(); wg.Wait(); _ = store.Close() }}
	t.Cleanup(rw.stop)

	deadline := time.Now().Add(15 * time.Second)
	for !client.Connected() && time.Now().Before(deadline) {
		time.Sleep(50 * time.Millisecond)
	}
	if !client.Connected() {
		t.Fatal("worker could not connect to RabbitMQ")
	}
	time.Sleep(300 * time.Millisecond) // consumers attach
	return rw
}

// ---- helpers for publishing / inspecting as the Todo API would ----------------------------------------

func newConn(t *testing.T, in *infra) *amqp.Channel {
	t.Helper()
	conn, err := amqp.Dial(in.amqpURL)
	if err != nil {
		t.Fatal(err)
	}
	ch, err := conn.Channel()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	return ch
}

func publishCreated(t *testing.T, in *infra, todoID string) []byte {
	t.Helper()
	body, _ := json.Marshal(todo.Created{EventID: todo.NewID(), EventType: todo.EventTodoCreated, TodoID: todoID, UserID: "user-1", CreatedAt: time.Now().UTC()})
	publishRaw(t, in, "todo.created", body)
	return body
}

func publishRaw(t *testing.T, in *infra, key string, body []byte) {
	t.Helper()
	ch := newConn(t, in)
	err := ch.PublishWithContext(context.Background(), todo.Exchange, key, true, false, amqp.Publishing{
		ContentType: "application/json", DeliveryMode: amqp.Persistent, MessageId: todo.NewID(), Body: body,
	})
	if err != nil {
		t.Fatal(err)
	}
	time.Sleep(100 * time.Millisecond)
}

func publishManyCreated(t *testing.T, in *infra, ids []string) {
	t.Helper()
	ch := newConn(t, in)
	for _, todoID := range ids {
		body, _ := json.Marshal(todo.Created{EventID: todo.NewID(), EventType: todo.EventTodoCreated, TodoID: todoID, UserID: "user-1", CreatedAt: time.Now().UTC()})
		if err := ch.PublishWithContext(context.Background(), todo.Exchange, "todo.created", true, false, amqp.Publishing{
			ContentType: "application/json", DeliveryMode: amqp.Persistent, MessageId: todo.NewID(), Body: body,
		}); err != nil {
			t.Fatal(err)
		}
	}
	time.Sleep(200 * time.Millisecond)
}

func publishRelease(t *testing.T, in *infra, scope, todoID string) {
	body, _ := json.Marshal(todo.ReleaseRequested{EventID: todo.NewID(), EventType: todo.EventReleaseRequested, Scope: scope, TodoID: todoID, RequestedBy: "admin-1", RequestedAt: time.Now().UTC()})
	publishRaw(t, in, "todo.release", body)
}

type fastProc struct {
	delay time.Duration
	calls atomic.Int32
	cur   atomic.Int32
	peak  atomic.Int32
	fail  func(call int32) error
}

func (p *fastProc) Process(ctx context.Context, _ worker.Job) error {
	n := p.cur.Add(1)
	for {
		pk := p.peak.Load()
		if n <= pk || p.peak.CompareAndSwap(pk, n) {
			break
		}
	}
	defer p.cur.Add(-1)
	call := p.calls.Add(1)
	select {
	case <-time.After(p.delay):
	case <-ctx.Done():
		return ctx.Err()
	}
	if p.fail != nil {
		return p.fail(call)
	}
	return nil
}

func id(prefix string) string { return prefix + "-" + todo.NewID()[:8] }

// =========================================================================================================

func TestEndToEnd_CreateProcessReleaseComplete(t *testing.T) {
	startWorker(t, shared, workerOpts{id: "w-e2e", proc: &fastProc{delay: 100 * time.Millisecond}})
	todoID := id("e2e")

	publishCreated(t, shared, todoID)
	shared.status.waitFor(t, todoID, todo.StatusWaitingRelease, 10*time.Second)
	if got := shared.status.get(todoID); got[0] != todo.StatusProcessing {
		t.Fatalf("sequence %v", got)
	}

	publishRelease(t, shared, todo.ScopeSingle, todoID)
	shared.status.waitFor(t, todoID, todo.StatusCompleted, 10*time.Second)
	got := shared.status.get(todoID)
	want := []todo.Status{todo.StatusProcessing, todo.StatusWaitingRelease, todo.StatusPersisting, todo.StatusCompleted}
	if fmt.Sprint(got) != fmt.Sprint(want) {
		t.Fatalf("sequence %v, want %v", got, want)
	}
}

func TestDuplicateMessageIsProcessedOnce(t *testing.T) {
	proc := &fastProc{delay: 100 * time.Millisecond}
	startWorker(t, shared, workerOpts{id: "w-dup", proc: proc})
	todoID := id("dup")

	body := publishCreated(t, shared, todoID)
	publishRaw(t, shared, "todo.created", body) // the broker delivers it twice
	publishRaw(t, shared, "todo.created", body)
	shared.status.waitFor(t, todoID, todo.StatusWaitingRelease, 10*time.Second)
	time.Sleep(1500 * time.Millisecond)

	got := shared.status.get(todoID)
	if count(got, todo.StatusProcessing) != 1 || count(got, todo.StatusWaitingRelease) != 1 {
		t.Fatalf("duplicates caused extra work: %v", got)
	}
	if proc.calls.Load() != 1 {
		t.Fatalf("processor ran %d times", proc.calls.Load())
	}
}

func TestSchedulerReleasesWithoutAnyAdminAction(t *testing.T) {
	startWorker(t, shared, workerOpts{id: "w-sched", proc: &fastProc{delay: 50 * time.Millisecond}, releaseInterval: 800 * time.Millisecond})
	todoID := id("sched")
	publishCreated(t, shared, todoID)
	shared.status.waitFor(t, todoID, todo.StatusCompleted, 15*time.Second)
}

func TestFailingJobIsRetriedThenFailedAndDeadLettered(t *testing.T) {
	proc := &fastProc{delay: 20 * time.Millisecond, fail: func(int32) error { return fmt.Errorf("boom") }}
	startWorker(t, shared, workerOpts{id: "w-fail", maxRetries: 2, proc: proc})
	todoID := id("fail")

	publishCreated(t, shared, todoID)
	shared.status.waitFor(t, todoID, todo.StatusFailed, 15*time.Second)

	if proc.calls.Load() != 3 { // first try + 2 retries
		t.Fatalf("attempts=%d, want 3", proc.calls.Load())
	}
	// ...and the message that could not be processed is parked in the DLQ for inspection.
	ch := newConn(t, shared)
	var found bool
	for i := 0; i < 20 && !found; i++ {
		d, ok, _ := ch.Get("todo.processing.dlq", true)
		if ok {
			var c todo.Created
			_ = json.Unmarshal(d.Body, &c)
			found = c.TodoID == todoID && d.Headers["x-death"] != nil
		} else {
			time.Sleep(100 * time.Millisecond)
		}
	}
	if !found {
		t.Fatal("exhausted message did not reach todo.processing.dlq with x-death diagnostics")
	}
}

func TestMalformedMessageGoesStraightToTheDLQ(t *testing.T) {
	proc := &fastProc{}
	startWorker(t, shared, workerOpts{id: "w-poison", proc: proc})
	publishRaw(t, shared, "todo.created", []byte(`{"this is":"not a todo"`))

	ch := newConn(t, shared)
	var got bool
	for i := 0; i < 50 && !got; i++ {
		d, ok, _ := ch.Get("todo.processing.dlq", true)
		got = ok && bytes.Contains(d.Body, []byte("not a todo"))
		if !ok {
			time.Sleep(100 * time.Millisecond)
		}
	}
	if !got {
		t.Fatal("poison message did not reach the DLQ")
	}
	if proc.calls.Load() != 0 {
		t.Fatal("poison message reached the processor")
	}
}

func TestConcurrencyIsBoundedUnderRealLoad(t *testing.T) {
	proc := &fastProc{delay: 300 * time.Millisecond}
	startWorker(t, shared, workerOpts{id: "w-load", concurrency: 5, proc: proc})
	ids := make([]string, 20)
	start := time.Now()
	for i := range ids {
		ids[i] = id("load")
	}
	publishManyCreated(t, shared, ids) // one burst, like 20 users creating todos at once
	for _, i := range ids {
		shared.status.waitFor(t, i, todo.StatusWaitingRelease, 30*time.Second)
	}
	elapsed := time.Since(start)
	if p := proc.peak.Load(); p != 5 {
		t.Fatalf("peak concurrency %d, want exactly the limit of 5 under a 20-job burst", p)
	}
	t.Logf("20 jobs x 300ms with concurrency 5: peak=%d elapsed=%s", proc.peak.Load(), elapsed.Round(time.Millisecond))
}

// A worker that dies mid-job (connection dropped, handler never settles) must not lose the message.
func TestWorkerCrashRedeliversTheUnacknowledgedMessage(t *testing.T) {
	stuck := make(chan struct{})
	var first atomic.Bool
	proc := procFunc(func(ctx context.Context, _ worker.Job) error {
		if first.CompareAndSwap(false, true) {
			<-stuck // "crashed": never returns, never acks, ignores its context
		}
		return nil
	})
	startWorker(t, shared, workerOpts{id: "w-crash", processTimeout: time.Second, proc: proc})
	t.Cleanup(func() { close(stuck) }) // runs before the worker stops, so shutdown can drain the "crashed" job
	todoID := id("crash")

	publishCreated(t, shared, todoID)
	shared.status.waitFor(t, todoID, todo.StatusProcessing, 10*time.Second)

	// The broker loses the worker's connection while the job is unacknowledged.
	if _, _, err := shared.rabbit.Exec(context.Background(), []string{"rabbitmqctl", "close_all_connections", "simulated crash"}); err != nil {
		t.Fatal(err)
	}

	// The message is redelivered; the dead worker's Redis lease expires (timeout+5s) and the job completes.
	shared.status.waitFor(t, todoID, todo.StatusWaitingRelease, 40*time.Second)
}

func TestWorkerReconnectsAfterABrokerOutage(t *testing.T) {
	rw := startWorker(t, shared, workerOpts{id: "w-outage", proc: &fastProc{delay: 50 * time.Millisecond}})

	if _, _, err := shared.rabbit.Exec(context.Background(), []string{"rabbitmqctl", "stop_app"}); err != nil {
		t.Fatal(err)
	}
	waitUntil(t, 20*time.Second, func() bool { return !rw.client.Connected() }, "worker should notice the broker is gone")

	if _, _, err := shared.rabbit.Exec(context.Background(), []string{"rabbitmqctl", "start_app"}); err != nil {
		t.Fatal(err)
	}
	waitUntil(t, 60*time.Second, rw.client.Connected, "worker should reconnect when the broker returns")

	todoID := id("outage")
	// publish may need a moment for the broker to accept connections again
	waitUntil(t, 30*time.Second, func() bool {
		conn, err := amqp.Dial(shared.amqpURL)
		if err != nil {
			return false
		}
		conn.Close()
		return true
	}, "broker accepts connections")
	time.Sleep(time.Second)
	publishCreated(t, shared, todoID)
	shared.status.waitFor(t, todoID, todo.StatusWaitingRelease, 30*time.Second)
}

func TestAnUnresponsiveRedisDoesNotLoseWork(t *testing.T) {
	startWorker(t, shared, workerOpts{id: "w-redis", processTimeout: 5 * time.Second, proc: &fastProc{delay: 50 * time.Millisecond}})
	rdb := goredis.NewClient(&goredis.Options{Addr: shared.redisAddr})
	defer rdb.Close()

	// Redis stops answering for 6 s (longer than the worker's 3 s I/O timeouts).
	if err := rdb.Do(context.Background(), "CLIENT", "PAUSE", "6000", "ALL").Err(); err != nil {
		t.Fatal(err)
	}
	todoID := id("redisdown")
	publishCreated(t, shared, todoID)

	time.Sleep(2 * time.Second)
	if got := shared.status.get(todoID); len(got) != 0 {
		t.Fatalf("worker made progress without Redis: %v", got)
	}
	// The message stayed with RabbitMQ; once Redis answers again the job completes exactly once.
	shared.status.waitFor(t, todoID, todo.StatusWaitingRelease, 40*time.Second)
	time.Sleep(time.Second)
	if c := count(shared.status.get(todoID), todo.StatusWaitingRelease); c != 1 {
		t.Fatalf("WAITING_RELEASE reported %d times", c)
	}
}

type procFunc func(context.Context, worker.Job) error

func (f procFunc) Process(ctx context.Context, j worker.Job) error { return f(ctx, j) }

func waitUntil(t *testing.T, d time.Duration, cond func() bool, msg string) {
	t.Helper()
	deadline := time.Now().Add(d)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(100 * time.Millisecond)
	}
	t.Fatal("timeout: " + msg)
}
