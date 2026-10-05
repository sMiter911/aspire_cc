using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TodoApi.Authorization;
using TodoApi.DTOs;
using TodoApi.Middleware;
using TodoApi.Services;

namespace TodoApi.Controllers;

/// <summary>
/// The caller's own todos; ADMIN may additionally address any todo by id. Ownership/cross-user rules are not
/// repeated here: they are enforced by policies plus <see cref="TodoService.GetAuthorizedAsync"/>.
/// A todo the caller may not access is reported as 404, indistinguishable from one that does not exist.
/// </summary>
[ApiController]
[Route("api/todos")]
[Produces("application/json")]
[ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiError>(StatusCodes.Status403Forbidden)]
public class TodosController(TodoService todos) : ControllerBase
{
    /// <summary>Create a todo owned by the authenticated user.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.CreateOwn)]
    [ProducesResponseType<TodoResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TodoResponse>> Create(CreateTodoRequest request, CancellationToken ct)
    {
        var todo = await todos.CreateAsync(User, request, ct);
        return CreatedAtAction(nameof(Get), new { id = todo.Id }, TodoResponse.From(todo));
    }

    /// <summary>List the authenticated user's own todos (newest first).</summary>
    [HttpGet]
    [Authorize(Policy = Policies.ReadOwn)]
    public Task<PageResponse<TodoResponse>> List(
        [FromQuery] bool? completed,
        [FromQuery, Range(0, 100_000)] int page = 0,
        [FromQuery, Range(1, 100)] int size = 20,
        CancellationToken ct = default) =>
        todos.ListOwnAsync(User, completed, page, size, ct);

    /// <summary>Get one todo (own, or any for ADMIN).</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.ReadOwn)]
    [ProducesResponseType<TodoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TodoResponse>> Get(Guid id, CancellationToken ct)
    {
        var todo = await todos.GetAuthorizedAsync(User, id, TodoOperations.Read, ct);
        return todo is null ? NotFound() : TodoResponse.From(todo);
    }

    /// <summary>Replace a todo's editable fields (own, or any for ADMIN). The owner never changes.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.UpdateOwn)]
    [ProducesResponseType<TodoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TodoResponse>> Update(Guid id, UpdateTodoRequest request, CancellationToken ct)
    {
        var todo = await todos.GetAuthorizedAsync(User, id, TodoOperations.Update, ct);
        if (todo is null)
        {
            return NotFound();
        }
        await todos.UpdateAsync(todo, request, User, ct);
        return TodoResponse.From(todo);
    }

    /// <summary>Mark a todo completed (own, or any for ADMIN).</summary>
    [HttpPatch("{id:guid}/complete")]
    [Authorize(Policy = Policies.UpdateOwn)]
    [ProducesResponseType<TodoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TodoResponse>> Complete(Guid id, CancellationToken ct)
    {
        var todo = await todos.GetAuthorizedAsync(User, id, TodoOperations.Update, ct);
        if (todo is null)
        {
            return NotFound();
        }
        await todos.CompleteAsync(todo, User, ct);
        return TodoResponse.From(todo);
    }

    /// <summary>Delete a todo (own, or any for ADMIN).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.DeleteOwn)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var todo = await todos.GetAuthorizedAsync(User, id, TodoOperations.Delete, ct);
        if (todo is null)
        {
            return NotFound();
        }
        await todos.DeleteAsync(todo, User, ct);
        return NoContent();
    }
}
