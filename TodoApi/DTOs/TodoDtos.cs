using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using TodoApi.Models;

namespace TodoApi.DTOs;

/// <summary>
/// Unknown JSON members are rejected (400). In particular a client sending "userId" never gets a silent
/// pass: the owner is always the authenticated subject.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record CreateTodoRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Title,
    [StringLength(2000)] string? Description,
    DateTimeOffset? DueDate);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record UpdateTodoRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Title,
    [StringLength(2000)] string? Description,
    DateTimeOffset? DueDate,
    bool IsCompleted);

/// <summary>What owners (and admins) see. <c>ProcessingStatus</c> is one of QUEUED, PROCESSING, WAITING_RELEASE,
/// PERSISTING, COMPLETED, FAILED.</summary>
public record TodoResponse(
    Guid Id,
    Guid UserId,
    string Title,
    string? Description,
    bool IsCompleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DueDate,
    string ProcessingStatus,
    DateTimeOffset? ProcessedAt)
{
    public static TodoResponse From(Todo t) =>
        new(t.Id, t.UserId, t.Title, t.Description, t.IsCompleted, t.CreatedAt, t.UpdatedAt, t.DueDate,
            t.ProcessingStatus.ToWire(), t.ProcessedAt);
}

/// <summary>Admin view: adds worker and attempt metadata that ordinary users never receive.</summary>
public record AdminTodoResponse(
    Guid Id,
    Guid UserId,
    string Title,
    string? Description,
    bool IsCompleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DueDate,
    string ProcessingStatus,
    DateTimeOffset? QueuedAt,
    string? WorkerId,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? ProcessedAt,
    string? ProcessingError,
    DateTimeOffset? ProcessingUpdatedAt)
{
    public static AdminTodoResponse From(Todo t) =>
        new(t.Id, t.UserId, t.Title, t.Description, t.IsCompleted, t.CreatedAt, t.UpdatedAt, t.DueDate,
            t.ProcessingStatus.ToWire(), t.QueuedAt, t.WorkerId, t.LastAttemptAt, t.ProcessedAt, t.ProcessingError,
            t.ProcessingUpdatedAt);
}

public record ReleaseAllResponse(int Waiting);

public record PageResponse<T>(IReadOnlyList<T> Content, int Page, int Size, long TotalElements, int TotalPages);
