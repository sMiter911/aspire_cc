using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TodoApi.Authorization;
using TodoApi.DTOs;
using TodoApi.Middleware;
using TodoApi.Services;

namespace TodoApi.Controllers;

/// <summary>
/// Cross-user views and operational actions. Every action requires ADMIN (policies below); everyone else gets 403.
/// The Go worker never sees a caller: this API authorizes first, then sends a command through the broker.
/// </summary>
[ApiController]
[Route("api/admin/todos")]
[Authorize(Policy = Policies.ReadAll)]
[Produces("application/json")]
[ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiError>(StatusCodes.Status403Forbidden)]
public class AdminTodosController(TodoService todos, TodoProcessingService processing) : ControllerBase
{
    /// <summary>List every user's todos with processing metadata; filter by owner and completion state.</summary>
    [HttpGet]
    public Task<PageResponse<AdminTodoResponse>> ListAll(
        [FromQuery] Guid? userId,
        [FromQuery] bool? completed,
        [FromQuery, Range(0, 100_000)] int page = 0,
        [FromQuery, Range(1, 100)] int size = 20,
        CancellationToken ct = default) =>
        todos.ListAllAsync(userId, completed, page, size, ct);

    /// <summary>Release one todo that is WAITING_RELEASE. Accepted (202): the worker performs it asynchronously.</summary>
    [HttpPost("{id:guid}/release")]
    [Authorize(Policy = Policies.Release)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct) =>
        await processing.RequestReleaseAsync(User, id, ct) switch
        {
            ReleaseResult.Requested => Accepted(),
            ReleaseResult.NotFound => NotFound(),
            _ => Conflict(ApiError.Create(409, "NOT_WAITING_RELEASE", "The todo is not waiting for release", Request.Path)),
        };

    /// <summary>Release every todo currently WAITING_RELEASE. Returns how many were waiting.</summary>
    [HttpPost("release")]
    [Authorize(Policy = Policies.Release)]
    [ProducesResponseType<ReleaseAllResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ReleaseAll(CancellationToken ct) =>
        Accepted(new ReleaseAllResponse(await processing.RequestReleaseAllAsync(User, ct)));

    /// <summary>Put a FAILED todo back in the queue.</summary>
    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = Policies.Retry)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) =>
        await processing.RetryAsync(User, id, ct) switch
        {
            RetryResult.Requested => Accepted(),
            RetryResult.NotFound => NotFound(),
            _ => Conflict(ApiError.Create(409, "NOT_FAILED", "Only a FAILED todo can be retried", Request.Path)),
        };
}
