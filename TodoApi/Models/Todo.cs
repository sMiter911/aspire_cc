namespace TodoApi.Models;

/// <summary>
/// A todo item. <see cref="UserId"/> is the immutable <c>sub</c> claim issued by the Spring Boot identity service.
/// This service stores no other user data and has no users table: identity is owned elsewhere.
/// </summary>
public class Todo
{
    public Guid Id { get; set; }

    /// <summary>Owner. Always taken from the validated access token, never from a request body.</summary>
    public Guid UserId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DueDate { get; set; }

    // ---- asynchronous processing (owned here; the Go worker reports, this service persists) ----------------

    public ProcessingStatus ProcessingStatus { get; set; } = ProcessingStatus.Queued;

    /// <summary>When the todo was (re-)queued for processing.</summary>
    public DateTimeOffset? QueuedAt { get; set; }

    /// <summary>The worker that last reported on this todo (diagnostics, admin only).</summary>
    public string? WorkerId { get; set; }

    /// <summary>When the worker last started an attempt.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>When processing finished (status COMPLETED).</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Short, sanitized reason when FAILED. Never an internal exception message.</summary>
    public string? ProcessingError { get; set; }

    public DateTimeOffset? ProcessingUpdatedAt { get; set; }
}
