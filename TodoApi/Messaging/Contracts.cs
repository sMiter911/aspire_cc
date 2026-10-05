using System.Text.Json;
using System.Text.Json.Serialization;
using TodoApi.Models;

namespace TodoApi.Messaging;

/// <summary>
/// The message contract shared with the Go worker (see /messaging/README.md). Identifiers and metadata only:
/// todo content never travels over the broker.
/// </summary>
public static class Topology
{
    public const string Exchange = "todo.events";
    public const string KeyCreated = "todo.created";
    public const string KeyRelease = "todo.release";
    public const string KeyStatusChanged = "todo.status.changed";
    public const string QueueStatus = "todo.status";
}

public static class EventTypes
{
    public const string TodoCreated = "TodoCreated";
    public const string ReleaseRequested = "ReleaseRequested";
    public const string StatusChanged = "TodoStatusChanged";
}

public record TodoCreatedMessage(Guid EventId, string EventType, Guid TodoId, Guid UserId, DateTimeOffset CreatedAt)
{
    public static TodoCreatedMessage For(Todo todo) =>
        new(Guid.NewGuid(), EventTypes.TodoCreated, todo.Id, todo.UserId, todo.CreatedAt);
}

/// <summary>Sent only after the API authorized an ADMIN. <c>Scope</c> is "single" or "all".</summary>
public record ReleaseRequestedMessage(
    Guid EventId, string EventType, string Scope, Guid? TodoId, Guid RequestedBy, DateTimeOffset RequestedAt)
{
    public static ReleaseRequestedMessage Single(Guid todoId, Guid admin, DateTimeOffset now) =>
        new(Guid.NewGuid(), EventTypes.ReleaseRequested, "single", todoId, admin, now);

    public static ReleaseRequestedMessage All(Guid admin, DateTimeOffset now) =>
        new(Guid.NewGuid(), EventTypes.ReleaseRequested, "all", null, admin, now);
}

/// <summary>Reported by the worker. Parsed leniently (unknown fields ignored), validated before use.</summary>
public record StatusChangedMessage(
    Guid EventId, string EventType, Guid TodoId, Guid? UserId, string Status, string? WorkerId,
    DateTimeOffset OccurredAt, string? Error);

public static class MessageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(ReadOnlySpan<byte> body) => JsonSerializer.Deserialize<T>(body, Options);
}
