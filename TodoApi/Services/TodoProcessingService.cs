using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TodoApi.Authorization;
using TodoApi.Data;
using TodoApi.Messaging;
using TodoApi.Models;

namespace TodoApi.Services;

public enum ApplyResult
{
    /// <summary>The transition was valid and has been persisted.</summary>
    Applied,
    /// <summary>The todo already is in that state (a repeated message): nothing to do.</summary>
    Duplicate,
    /// <summary>Stale or out-of-order report (would move the todo backwards or change a terminal state).</summary>
    Ignored,
    /// <summary>The todo does not exist (deleted meanwhile).</summary>
    NotFound,
}

public enum ReleaseResult { Requested, NotFound, NotWaitingRelease }
public enum RetryResult { Requested, NotFound, NotFailed }

/// <summary>
/// The Todo API owns the processing lifecycle. The Go worker reports what happened; this service decides whether
/// that is a legal transition and persists it. Every write is a single atomic SQL UPDATE guarded by the allowed
/// source states, so concurrent or repeated messages cannot corrupt the state.
/// </summary>
public class TodoProcessingService(TodoDbContext db, TimeProvider time, ILogger<TodoProcessingService> logger)
{
    public async Task<ApplyResult> ApplyStatusAsync(StatusChangedMessage msg, CancellationToken ct)
    {
        if (!ProcessingStatuses.TryParse(msg.Status, out var target) || target == ProcessingStatus.Queued)
        {
            throw new ArgumentException("Invalid status in message"); // consumer dead-letters malformed messages
        }

        var todo = await db.Todos.AsNoTracking().Where(t => t.Id == msg.TodoId)
            .Select(t => new { t.UserId, t.ProcessingStatus }).FirstOrDefaultAsync(ct);
        if (todo is null)
        {
            logger.LogInformation("Status {Status} for unknown todo {TodoId} ignored (deleted?)", msg.Status, msg.TodoId);
            return ApplyResult.NotFound;
        }
        if (msg.UserId is { } owner && owner != todo.UserId)
        {
            // The message claims a different owner than the todo has: never trust it.
            logger.LogWarning("Status message for todo {TodoId} carries a mismatching user id; ignored", msg.TodoId);
            return ApplyResult.Ignored;
        }

        var now = time.GetUtcNow();
        var from = ProcessingStatuses.AcceptedFrom(target);
        var error = target == ProcessingStatus.Failed ? Truncate(msg.Error, 200) ?? "processing failed" : null;

        var updated = await db.Todos
            .Where(t => t.Id == msg.TodoId && from.Contains(t.ProcessingStatus))
            .ExecuteUpdateAsync(set => set
                .SetProperty(t => t.ProcessingStatus, target)
                .SetProperty(t => t.WorkerId, Truncate(msg.WorkerId, 100))
                .SetProperty(t => t.ProcessingUpdatedAt, now)
                .SetProperty(t => t.LastAttemptAt, t => target == ProcessingStatus.Processing ? msg.OccurredAt : t.LastAttemptAt)
                .SetProperty(t => t.ProcessedAt, t => target == ProcessingStatus.Completed ? msg.OccurredAt : t.ProcessedAt)
                .SetProperty(t => t.ProcessingError, error), ct);

        if (updated == 1)
        {
            logger.LogInformation("Todo {TodoId} {From} -> {To} (worker {Worker})", msg.TodoId, todo.ProcessingStatus.ToWire(), msg.Status, msg.WorkerId);
            return ApplyResult.Applied;
        }

        // Nothing matched: it is either a repeat or a stale/illegal move. A new PROCESSING attempt while already
        // PROCESSING (a retry) only refreshes the attempt timestamp.
        var current = await db.Todos.AsNoTracking().Where(t => t.Id == msg.TodoId).Select(t => t.ProcessingStatus).FirstAsync(ct);
        if (current == target && target == ProcessingStatus.Processing)
        {
            await db.Todos.Where(t => t.Id == msg.TodoId && t.ProcessingStatus == ProcessingStatus.Processing)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.LastAttemptAt, msg.OccurredAt).SetProperty(t => t.WorkerId, Truncate(msg.WorkerId, 100)), ct);
            return ApplyResult.Duplicate;
        }
        if (current == target)
        {
            return ApplyResult.Duplicate;
        }
        logger.LogWarning("Todo {TodoId}: report {To} ignored while {Current} (stale or out of order)", msg.TodoId, msg.Status, current.ToWire());
        return ApplyResult.Ignored;
    }

    /// <summary>
    /// Admin: ask the worker to release a todo that is waiting. The caller must already have passed the ADMIN policy;
    /// this method re-checks the todo state and records who asked. The command goes through the outbox.
    /// </summary>
    public async Task<ReleaseResult> RequestReleaseAsync(ClaimsPrincipal admin, Guid todoId, CancellationToken ct)
    {
        var status = await db.Todos.AsNoTracking().Where(t => t.Id == todoId).Select(t => (ProcessingStatus?)t.ProcessingStatus).FirstOrDefaultAsync(ct);
        if (status is null) return ReleaseResult.NotFound;
        if (status != ProcessingStatus.WaitingRelease) return ReleaseResult.NotWaitingRelease;

        var now = time.GetUtcNow();
        db.AddOutbox(Topology.KeyRelease, EventTypes.ReleaseRequested, ReleaseRequestedMessage.Single(todoId, admin.GetUserId(), now), now);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Release of todo {TodoId} requested by admin {AdminId}", todoId, admin.GetUserId());
        return ReleaseResult.Requested;
    }

    /// <summary>Admin: ask the worker to release everything currently waiting. Returns how many todos are waiting.</summary>
    public async Task<int> RequestReleaseAllAsync(ClaimsPrincipal admin, CancellationToken ct)
    {
        var waiting = await db.Todos.CountAsync(t => t.ProcessingStatus == ProcessingStatus.WaitingRelease, ct);
        if (waiting == 0) return 0;

        var now = time.GetUtcNow();
        db.AddOutbox(Topology.KeyRelease, EventTypes.ReleaseRequested, ReleaseRequestedMessage.All(admin.GetUserId(), now), now);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Release of all waiting todos ({Count}) requested by admin {AdminId}", waiting, admin.GetUserId());
        return waiting;
    }

    /// <summary>Admin: put a FAILED todo back in the queue (same state change + new TodoCreated message, atomically).</summary>
    public async Task<RetryResult> RetryAsync(ClaimsPrincipal admin, Guid todoId, CancellationToken ct)
    {
        var exists = await db.Todos.AnyAsync(t => t.Id == todoId, ct);
        if (!exists) return RetryResult.NotFound;

        var now = time.GetUtcNow();
        // The Npgsql integration enables a retrying execution strategy, so a manual transaction must run inside it.
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.Todos.Where(t => t.Id == todoId && t.ProcessingStatus == ProcessingStatus.Failed)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(t => t.ProcessingStatus, ProcessingStatus.Queued)
                    .SetProperty(t => t.QueuedAt, now)
                    .SetProperty(t => t.ProcessingError, (string?)null)
                    .SetProperty(t => t.WorkerId, (string?)null)
                    .SetProperty(t => t.ProcessingUpdatedAt, now), ct);
            if (changed == 0) return RetryResult.NotFailed;

            var todo = await db.Todos.AsNoTracking().FirstAsync(t => t.Id == todoId, ct);
            db.AddOutbox(Topology.KeyCreated, EventTypes.TodoCreated, TodoCreatedMessage.For(todo), now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return RetryResult.Requested;
        });
        if (result == RetryResult.Requested)
        {
            logger.LogInformation("Todo {TodoId} re-queued by admin {AdminId}", todoId, admin.GetUserId());
        }
        return result;
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
