// Package rabbitmq wraps amqp091 with what the worker needs: a self-healing connection, confirmed publishing,
// and a consumer that survives broker restarts. The broker topology is NOT declared here; it is loaded by the
// broker from messaging/rabbitmq/definitions.json.
package rabbitmq

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net/url"
	"sync"
	"sync/atomic"
	"time"

	amqp "github.com/rabbitmq/amqp091-go"
)

type Config struct {
	Host, User, Password, VHost string
	Port                        int
	ConnectionName              string
}

func (c Config) uri() string {
	u := url.URL{
		Scheme: "amqp",
		User:   url.UserPassword(c.User, c.Password), // escapes special characters
		Host:   fmt.Sprintf("%s:%d", c.Host, c.Port),
		Path:   "/" + url.PathEscape(c.VHost),
	}
	if c.VHost == "/" {
		u.Path = "/"
		u.RawPath = "/%2F"
	}
	return u.String()
}

// Publishing is what callers hand to Publish.
type Publishing struct {
	MessageID string
	Type      string
	Body      []byte
	Headers   map[string]any
}

// ErrNotConnected is returned when there is no live broker connection.
var ErrNotConnected = errors.New("rabbitmq: not connected")

type session struct {
	conn  *amqp.Connection
	pubCh *amqp.Channel
	mu    sync.Mutex // serialises publishes on pubCh
	// returned remembers message ids the broker could not route (mandatory publish).
	returned sync.Map
}

// Client keeps one connection alive (reconnecting with backoff) and hands out channels from it.
type Client struct {
	cfg       Config
	log       *slog.Logger
	connected atomic.Bool
	mu        sync.RWMutex
	sess      *session
	changed   chan struct{} // closed and replaced whenever the session changes
}

func NewClient(cfg Config, log *slog.Logger) *Client {
	return &Client{cfg: cfg, log: log, changed: make(chan struct{})}
}

// Connected reports whether a broker connection is currently established (used by /ready).
func (c *Client) Connected() bool { return c.connected.Load() }

// Run maintains the connection until ctx is cancelled. Call it in its own goroutine.
func (c *Client) Run(ctx context.Context) {
	backoff := time.Second
	for ctx.Err() == nil {
		s, err := c.dial()
		if err != nil {
			c.log.Warn("rabbitmq connect failed", "error", err, "retry_in", backoff.String())
			if !sleep(ctx, backoff) {
				return
			}
			backoff = min(backoff*2, 15*time.Second)
			continue
		}
		backoff = time.Second
		closed := s.conn.NotifyClose(make(chan *amqp.Error, 1))
		c.setSession(s)
		c.log.Info("rabbitmq connected", "host", c.cfg.Host)

		select {
		case <-ctx.Done():
			c.setSession(nil)
			_ = s.conn.Close()
			return
		case err := <-closed:
			c.setSession(nil)
			c.log.Warn("rabbitmq connection lost", "error", err)
		}
	}
}

func (c *Client) dial() (*session, error) {
	conn, err := amqp.DialConfig(c.cfg.uri(), amqp.Config{
		Heartbeat:  10 * time.Second,
		Dial:       amqp.DefaultDial(5 * time.Second),
		Properties: amqp.Table{"connection_name": c.cfg.ConnectionName},
	})
	if err != nil {
		return nil, err
	}
	ch, err := conn.Channel()
	if err != nil {
		_ = conn.Close()
		return nil, err
	}
	if err := ch.Confirm(false); err != nil { // publisher confirms: a publish is only "done" once the broker has it
		_ = conn.Close()
		return nil, err
	}
	s := &session{conn: conn, pubCh: ch}
	returns := ch.NotifyReturn(make(chan amqp.Return, 64))
	go func() {
		for r := range returns {
			s.returned.Store(r.MessageId, r.ReplyText)
		}
	}()
	return s, nil
}

func (c *Client) setSession(s *session) {
	c.mu.Lock()
	c.sess = s
	old := c.changed
	c.changed = make(chan struct{})
	c.mu.Unlock()
	c.connected.Store(s != nil)
	close(old)
}

func (c *Client) current() (*session, chan struct{}) {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.sess, c.changed
}

// waitSession blocks until a connection is available (or ctx ends).
func (c *Client) waitSession(ctx context.Context) (*session, error) {
	for {
		s, changed := c.current()
		if s != nil {
			return s, nil
		}
		select {
		case <-changed:
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
}

// Publish sends a persistent message and returns only after the broker confirmed it. A message the broker cannot
// route to any queue is reported as an error (mandatory publish), never silently dropped.
func (c *Client) Publish(ctx context.Context, exchange, key string, p Publishing) error {
	s, _ := c.current()
	if s == nil {
		return ErrNotConnected
	}
	s.mu.Lock()
	defer s.mu.Unlock()

	headers := amqp.Table{}
	for k, v := range p.Headers {
		headers[k] = v
	}
	dc, err := s.pubCh.PublishWithDeferredConfirmWithContext(ctx, exchange, key, true, false, amqp.Publishing{
		ContentType:  "application/json",
		DeliveryMode: amqp.Persistent,
		MessageId:    p.MessageID,
		Type:         p.Type,
		Timestamp:    time.Now().UTC(),
		Headers:      headers,
		Body:         p.Body,
	})
	if err != nil {
		return fmt.Errorf("publish: %w", err)
	}
	acked, err := dc.WaitContext(ctx)
	if err != nil {
		return fmt.Errorf("await confirm: %w", err)
	}
	if !acked {
		return errors.New("broker nacked the publish")
	}
	// Returns are delivered before the confirm, so by now an unroutable message has been recorded.
	if reason, ok := s.returned.LoadAndDelete(p.MessageID); ok {
		return fmt.Errorf("unroutable message %s: %v", p.MessageID, reason)
	}
	return nil
}

// Consume reads queue until ctx is cancelled, reconnecting after broker failures. At most prefetch handlers run
// at once (see Dispatch). It returns nil on a clean shutdown.
func (c *Client) Consume(ctx context.Context, queue string, prefetch int, h Handler) error {
	pool := NewPool(prefetch)
	defer pool.Wait() // graceful shutdown: let running jobs finish
	backoff := time.Second
	for ctx.Err() == nil {
		err := c.consumeOnce(ctx, queue, prefetch, pool, h)
		if ctx.Err() != nil {
			return nil
		}
		c.log.Warn("consumer stopped, will retry", "queue", queue, "error", err, "retry_in", backoff.String())
		if !sleep(ctx, backoff) {
			return nil
		}
		backoff = min(backoff*2, 15*time.Second)
	}
	return nil
}

func (c *Client) consumeOnce(ctx context.Context, queue string, prefetch int, pool *Pool, h Handler) error {
	s, err := c.waitSession(ctx)
	if err != nil {
		return err
	}
	ch, err := s.conn.Channel()
	if err != nil {
		return err
	}
	defer ch.Close()

	// Prefetch = concurrency: the broker never has more unacknowledged messages with us than we may run.
	if err := ch.Qos(prefetch, 0, false); err != nil {
		return err
	}
	tag := fmt.Sprintf("%s-%s", c.cfg.ConnectionName, queue)
	msgs, err := ch.Consume(queue, tag, false, false, false, false, nil) // autoAck=false: manual acknowledgements
	if err != nil {
		return err
	}
	c.log.Info("consuming", "queue", queue, "prefetch", prefetch)

	deliveries := make(chan Delivery)
	go func() {
		defer close(deliveries)
		for m := range msgs {
			deliveries <- wrap(m)
		}
	}()
	go func() {
		select {
		case <-ctx.Done():
			_ = ch.Cancel(tag, false) // stop new deliveries; in-flight ones finish, then msgs closes
		case <-ch.NotifyClose(make(chan *amqp.Error, 1)):
		}
	}()

	DispatchPool(ctx, deliveries, pool, h, c.log) // returns when the channel closes; running jobs keep their slots
	return errors.New("delivery stream ended")
}

func wrap(m amqp.Delivery) Delivery {
	return Delivery{
		Body:        m.Body,
		MessageID:   m.MessageId,
		Type:        m.Type,
		RoutingKey:  m.RoutingKey,
		Headers:     m.Headers,
		Redelivered: m.Redelivered,
		settle: func(o Outcome) error {
			switch o {
			case Ack:
				return m.Ack(false)
			case Requeue:
				return m.Nack(false, true)
			default:
				return m.Nack(false, false)
			}
		},
	}
}

func sleep(ctx context.Context, d time.Duration) bool {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-t.C:
		return true
	}
}
