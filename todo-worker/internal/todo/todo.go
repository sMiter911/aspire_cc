// Package todo defines the message contract shared with the ASP.NET Core Todo API (see /messaging/README.md).
// The worker never owns the Todo domain: it reads identifiers from messages and reports status transitions back.
package todo

import "time"

// Status is the processing state of a todo. The Todo API owns the state machine and persists it;
// the worker only reports transitions.
type Status string

const (
	StatusQueued         Status = "QUEUED"
	StatusProcessing     Status = "PROCESSING"
	StatusWaitingRelease Status = "WAITING_RELEASE"
	StatusPersisting     Status = "PERSISTING"
	StatusCompleted      Status = "COMPLETED"
	StatusFailed         Status = "FAILED"
)

// Exchange and routing keys (topology itself lives in messaging/rabbitmq/definitions.json).
const (
	Exchange         = "todo.events"
	KeyStatusChanged = "todo.status.changed"
	QueueProcessing  = "todo.processing"
	QueueRelease     = "todo.release"
	// RetryKeyPrefix + "1".."3" selects a delay tier (5s, 30s, 2m in definitions.json).
	RetryKeyPrefix = "todo.retry."
	MaxRetryTiers  = 3
)

const (
	EventTodoCreated      = "TodoCreated"
	EventReleaseRequested = "ReleaseRequested"
	EventStatusChanged    = "TodoStatusChanged"
)

// Release scopes.
const (
	ScopeSingle = "single"
	ScopeAll    = "all"
)

// Created is the work message: identifiers and metadata only.
type Created struct {
	EventID   string    `json:"eventId"`
	EventType string    `json:"eventType"`
	TodoID    string    `json:"todoId"`
	UserID    string    `json:"userId"`
	CreatedAt time.Time `json:"createdAt"`
}

// ReleaseRequested is sent by the API after it authorized an ADMIN. The worker trusts the broker boundary,
// not the payload: it never authenticates users and ignores any claim about who may release.
type ReleaseRequested struct {
	EventID     string    `json:"eventId"`
	EventType   string    `json:"eventType"`
	Scope       string    `json:"scope"`
	TodoID      string    `json:"todoId,omitempty"`
	RequestedBy string    `json:"requestedBy"`
	RequestedAt time.Time `json:"requestedAt"`
}

// StatusChanged is published by the worker; the Todo API validates the transition and persists it.
type StatusChanged struct {
	EventID    string    `json:"eventId"`
	EventType  string    `json:"eventType"`
	TodoID     string    `json:"todoId"`
	UserID     string    `json:"userId,omitempty"`
	Status     Status    `json:"status"`
	WorkerID   string    `json:"workerId"`
	OccurredAt time.Time `json:"occurredAt"`
	Error      string    `json:"error,omitempty"`
}

// Record is the transient job state kept in Redis (todo:{id}:status).
type Record struct {
	Status      Status     `json:"status"`
	UserID      string     `json:"userId,omitempty"`
	EventID     string     `json:"eventId,omitempty"`
	QueuedAt    time.Time  `json:"queuedAt"`
	ProcessedAt *time.Time `json:"processedAt,omitempty"`
	WorkerID    string     `json:"workerId"`
	Attempt     int        `json:"attempt"`
	Error       string     `json:"error,omitempty"`
}

// Terminal reports whether a stored status ends the job.
func (s Status) Terminal() bool { return s == StatusCompleted || s == StatusFailed }
