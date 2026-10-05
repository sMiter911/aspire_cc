using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TodoApi.Authorization;
using TodoApi.Data;
using TodoApi.DTOs;
using TodoApi.Messaging;
using TodoApi.Models;

namespace TodoApi.Services;

public class TodoService(
    TodoDbContext db,
    IAuthorizationService authorization,
    TimeProvider time,
    ILogger<TodoService> logger)
{
    public async Task<Todo> CreateAsync(ClaimsPrincipal user, CreateTodoRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var todo = new Todo
        {
            Id = Guid.NewGuid(),
            UserId = user.GetUserId(), // ownership comes from the validated token, nothing else
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            DueDate = request.DueDate,
            CreatedAt = now,
            UpdatedAt = now,
            ProcessingStatus = ProcessingStatus.Queued,
            QueuedAt = now,
            ProcessingUpdatedAt = now,
        };
        db.Todos.Add(todo);
        // Same transaction as the todo: the message exists if and only if the todo was saved (transactional outbox).
        db.AddOutbox(Topology.KeyCreated, EventTypes.TodoCreated, TodoCreatedMessage.For(todo), now);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Todo {TodoId} created by user {UserId}", todo.Id, todo.UserId);
        return todo;
    }

    /// <summary>
    /// Loads a todo only if the caller may perform <paramref name="operation"/> on it:
    /// owner (resource handler) OR holder of the matching cross-user policy (ADMIN).
    /// "Not there" and "not yours" both return null, so callers answer 404 for both and an attacker cannot probe
    /// which ids exist (IDOR). Every per-todo endpoint goes through here: the rule lives in one place.
    /// </summary>
    public async Task<Todo?> GetAuthorizedAsync(
        ClaimsPrincipal user, Guid id, OperationAuthorizationRequirement operation, CancellationToken ct)
    {
        var todo = await db.Todos.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (todo is null)
        {
            return null;
        }

        if ((await authorization.AuthorizeAsync(user, todo, operation)).Succeeded)
        {
            return todo;
        }

        var crossUserPolicy = operation.Name switch
        {
            nameof(TodoOperations.Read) => Policies.ReadAll,
            nameof(TodoOperations.Update) => Policies.UpdateAll,
            nameof(TodoOperations.Delete) => Policies.DeleteAll,
            _ => null,
        };
        if (crossUserPolicy is not null && (await authorization.AuthorizeAsync(user, crossUserPolicy)).Succeeded)
        {
            return todo;
        }

        // Ids only: no titles, no tokens.
        logger.LogWarning("Authorization denied: user {UserId} attempted {Operation} on todo {TodoId}",
            user.FindFirst(Claims.Subject)?.Value, operation.Name, todo.Id);
        return null;
    }

    public Task<PageResponse<TodoResponse>> ListOwnAsync(
        ClaimsPrincipal user, bool? completed, int page, int size, CancellationToken ct) =>
        ListAsync(db.Todos.Where(t => t.UserId == user.GetUserId()), completed, page, size, TodoResponse.From, ct);

    /// <summary>Cross-user listing. Callers must already have passed the Todo.ReadAll policy.</summary>
    public Task<PageResponse<AdminTodoResponse>> ListAllAsync(
        Guid? userId, bool? completed, int page, int size, CancellationToken ct)
    {
        IQueryable<Todo> query = db.Todos;
        if (userId is { } id)
        {
            query = query.Where(t => t.UserId == id);
        }
        return ListAsync(query, completed, page, size, AdminTodoResponse.From, ct);
    }

    public async Task UpdateAsync(Todo todo, UpdateTodoRequest request, ClaimsPrincipal actor, CancellationToken ct)
    {
        todo.Title = request.Title.Trim();
        todo.Description = request.Description?.Trim();
        todo.DueDate = request.DueDate;
        todo.IsCompleted = request.IsCompleted;
        todo.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Todo {TodoId} updated by user {UserId}", todo.Id, actor.GetUserId());
    }

    public async Task CompleteAsync(Todo todo, ClaimsPrincipal actor, CancellationToken ct)
    {
        todo.IsCompleted = true;
        todo.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Todo {TodoId} completed by user {UserId}", todo.Id, actor.GetUserId());
    }

    public async Task DeleteAsync(Todo todo, ClaimsPrincipal actor, CancellationToken ct)
    {
        db.Todos.Remove(todo);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Todo {TodoId} deleted by user {UserId}", todo.Id, actor.GetUserId());
    }

    private static async Task<PageResponse<T>> ListAsync<T>(
        IQueryable<Todo> query, bool? completed, int page, int size, Func<Todo, T> map, CancellationToken ct)
    {
        if (completed is { } c)
        {
            query = query.Where(t => t.IsCompleted == c);
        }
        var total = await query.LongCountAsync(ct);
        var items = await query
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Skip(page * size).Take(size)
            .ToListAsync(ct);
        var pages = (int)Math.Ceiling(total / (double)size);
        return new PageResponse<T>(items.Select(map).ToList(), page, size, total, pages);
    }
}
