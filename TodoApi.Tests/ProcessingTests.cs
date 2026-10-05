using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TodoApi.Data;
using TodoApi.Messaging;
using TodoApi.Models;
using TodoApi.Services;

namespace TodoApi.Tests;

/// <summary>
/// Asynchronous-processing behavior that does not need a broker: creation + outbox, the status state machine,
/// admin release/retry authorization. (Broker behavior is covered by MessagingIntegrationTests.)
/// </summary>
[Collection("api")]
public class ProcessingTests(TodoApiFactory factory)
{
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    private HttpClient A => factory.ClientFor(Tokens.User(_userA));
    private HttpClient B => factory.ClientFor(Tokens.User(_userB));
    private HttpClient Admin => factory.ClientFor(Tokens.Admin(_admin));

    // ---- helpers -------------------------------------------------------------------------------------

    private async Task<T> WithDb<T>(Func<TodoDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TodoDbContext>());
    }

    private Task SetStatus(Guid todoId, ProcessingStatus status) =>
        WithDb(async db => await db.Todos.Where(t => t.Id == todoId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ProcessingStatus, status)));

    private Task<List<OutboxMessage>> OutboxFor(string routingKey, Func<OutboxMessage, bool>? filter = null) =>
        WithDb(async db =>
        {
            var rows = await db.Outbox.AsNoTracking().Where(m => m.RoutingKey == routingKey).ToListAsync();
            return rows.Where(filter ?? (_ => true)).ToList();
        });

    private async Task<StatusChangedMessage> Msg(Guid todoId, string status, Guid? user = null, string? error = null, string worker = "worker-1") =>
        await Task.FromResult(new StatusChangedMessage(Guid.NewGuid(), EventTypes.StatusChanged, todoId, user, status, worker, DateTimeOffset.UtcNow, error));

    private Task<ApplyResult> Apply(StatusChangedMessage m) =>
        WithDb(async db => await new TodoProcessingService(db, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TodoProcessingService>.Instance).ApplyStatusAsync(m, CancellationToken.None));

    private Task<Todo> Load(Guid id) => WithDb(db => db.Todos.AsNoTracking().FirstAsync(t => t.Id == id));

    // ---- creation: QUEUED + outbox ------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_todo_returns_QUEUED_and_records_a_TodoCreated_outbox_message()
    {
        var response = await A.PostAsJsonAsync("/api/todos", new { title = "Learn Go concurrency", description = "secret-ish text" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var todo = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = todo.GetProperty("id").GetGuid();
        Assert.Equal("QUEUED", todo.GetProperty("processingStatus").GetString());

        var rows = await OutboxFor("todo.created", m => m.Payload.Contains(id.ToString()));
        var row = Assert.Single(rows);
        Assert.Equal("todo.events", row.Exchange);
        Assert.Equal("TodoCreated", row.EventType);
        Assert.Null(row.PublishedAt); // no broker in this test: it waits in the outbox

        var payload = JsonDocument.Parse(row.Payload).RootElement;
        Assert.Equal(id, payload.GetProperty("todoId").GetGuid());
        Assert.Equal(_userA, payload.GetProperty("userId").GetGuid());
        Assert.NotEqual(Guid.Empty, payload.GetProperty("eventId").GetGuid());
        Assert.Equal("TodoCreated", payload.GetProperty("eventType").GetString());
        // identifiers and metadata only: todo content never goes over the broker
        Assert.DoesNotContain("Learn Go", row.Payload);
        Assert.DoesNotContain("secret-ish", row.Payload);
    }

    [Fact]
    public async Task A_rejected_create_leaves_no_outbox_message()
    {
        var before = (await OutboxFor("todo.created")).Count;
        var response = await A.PostAsJsonAsync("/api/todos", new { title = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, (await OutboxFor("todo.created")).Count);
    }

    [Fact]
    public async Task Users_see_status_but_not_worker_metadata_while_admins_see_both()
    {
        var id = await Json.CreateTodoAsync(A, "meta");
        await WithDb(db => db.Todos.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.WorkerId, "worker-42").SetProperty(t => t.ProcessingStatus, ProcessingStatus.WaitingRelease)));

        var asOwner = await A.GetFromJsonAsync<JsonElement>($"/api/todos/{id}");
        Assert.Equal("WAITING_RELEASE", asOwner.GetProperty("processingStatus").GetString());
        Assert.False(asOwner.TryGetProperty("workerId", out _));
        Assert.False(asOwner.TryGetProperty("processingError", out _));

        var adminList = await Admin.GetFromJsonAsync<JsonElement>($"/api/admin/todos?userId={_userA}&size=100");
        var row = adminList.GetProperty("content").EnumerateArray().First(t => t.GetProperty("id").GetGuid() == id);
        Assert.Equal("worker-42", row.GetProperty("workerId").GetString());
        Assert.True(row.TryGetProperty("queuedAt", out _));
        Assert.True(row.TryGetProperty("lastAttemptAt", out _));
    }

    [Fact]
    public async Task Clients_cannot_set_the_processing_status()
    {
        var id = await Json.CreateTodoAsync(A);
        var response = await A.PutAsync($"/api/todos/{id}",
            Json.Body("""{"title":"t","isCompleted":true,"processingStatus":"COMPLETED"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProcessingStatus.Queued, (await Load(id)).ProcessingStatus);
    }

    // ---- release authorization ----------------------------------------------------------------------

    [Fact]
    public async Task User_cannot_release_any_todo_not_even_their_own()
    {
        var mine = await Json.CreateTodoAsync(A);
        var theirs = await Json.CreateTodoAsync(B);
        await SetStatus(mine, ProcessingStatus.WaitingRelease);
        await SetStatus(theirs, ProcessingStatus.WaitingRelease);

        Assert.Equal(HttpStatusCode.Forbidden, (await A.PostAsync($"/api/admin/todos/{mine}/release", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await A.PostAsync($"/api/admin/todos/{theirs}/release", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await A.PostAsync("/api/admin/todos/release", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await A.PostAsync($"/api/admin/todos/{mine}/retry", null)).StatusCode);

        Assert.Empty(await OutboxFor("todo.release", m => m.Payload.Contains(mine.ToString()) || m.Payload.Contains(theirs.ToString())));
    }

    [Fact]
    public async Task Anonymous_callers_cannot_release()
    {
        var anon = factory.ClientFor();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync($"/api/admin/todos/{Guid.NewGuid()}/release", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/admin/todos/release", null)).StatusCode);
    }

    [Fact]
    public async Task Admin_release_publishes_a_command_that_records_who_asked()
    {
        var id = await Json.CreateTodoAsync(A);
        await SetStatus(id, ProcessingStatus.WaitingRelease);

        var response = await Admin.PostAsync($"/api/admin/todos/{id}/release", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var row = Assert.Single(await OutboxFor("todo.release", m => m.Payload.Contains(id.ToString())));
        var payload = JsonDocument.Parse(row.Payload).RootElement;
        Assert.Equal("ReleaseRequested", payload.GetProperty("eventType").GetString());
        Assert.Equal("single", payload.GetProperty("scope").GetString());
        Assert.Equal(_admin, payload.GetProperty("requestedBy").GetGuid()); // from the validated token
    }

    [Fact]
    public async Task Release_is_refused_for_missing_todos_and_todos_that_are_not_waiting()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PostAsync($"/api/admin/todos/{Guid.NewGuid()}/release", null)).StatusCode);

        var id = await Json.CreateTodoAsync(A); // still QUEUED
        var response = await Admin.PostAsync($"/api/admin/todos/{id}/release", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("NOT_WAITING_RELEASE", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty(await OutboxFor("todo.release", m => m.Payload.Contains(id.ToString())));
    }

    [Fact]
    public async Task Admin_can_release_everything_that_is_waiting()
    {
        var id = await Json.CreateTodoAsync(A);
        await SetStatus(id, ProcessingStatus.WaitingRelease);
        var before = (await OutboxFor("todo.release", m => m.Payload.Contains("\"scope\":\"all\""))).Count;

        var response = await Admin.PostAsync("/api/admin/todos/release", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("waiting").GetInt32() >= 1);
        Assert.Equal(before + 1, (await OutboxFor("todo.release", m => m.Payload.Contains("\"scope\":\"all\""))).Count);
    }

    // ---- retry ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_can_retry_a_failed_todo_which_is_queued_again_with_a_new_message()
    {
        var id = await Json.CreateTodoAsync(A);
        await WithDb(db => db.Todos.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.ProcessingStatus, ProcessingStatus.Failed).SetProperty(t => t.ProcessingError, "processing failed")));
        var before = (await OutboxFor("todo.created", m => m.Payload.Contains(id.ToString()))).Count;

        var response = await Admin.PostAsync($"/api/admin/todos/{id}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var todo = await Load(id);
        Assert.Equal(ProcessingStatus.Queued, todo.ProcessingStatus);
        Assert.Null(todo.ProcessingError);
        Assert.Equal(before + 1, (await OutboxFor("todo.created", m => m.Payload.Contains(id.ToString()))).Count);
    }

    [Fact]
    public async Task Only_failed_todos_can_be_retried()
    {
        var id = await Json.CreateTodoAsync(A);
        Assert.Equal(HttpStatusCode.Conflict, (await Admin.PostAsync($"/api/admin/todos/{id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PostAsync($"/api/admin/todos/{Guid.NewGuid()}/retry", null)).StatusCode);
    }

    // ---- status state machine (what the consumer applies) ------------------------------------------------

    [Fact]
    public async Task Reported_statuses_walk_the_lifecycle_and_are_idempotent()
    {
        var id = await Json.CreateTodoAsync(A);

        Assert.Equal(ApplyResult.Applied, await Apply(await Msg(id, "PROCESSING", _userA)));
        Assert.Equal(ApplyResult.Duplicate, await Apply(await Msg(id, "PROCESSING", _userA))); // redelivered
        Assert.Equal(ApplyResult.Applied, await Apply(await Msg(id, "WAITING_RELEASE", _userA)));
        Assert.Equal(ApplyResult.Duplicate, await Apply(await Msg(id, "WAITING_RELEASE", _userA)));
        Assert.Equal(ApplyResult.Applied, await Apply(await Msg(id, "PERSISTING", _userA)));
        Assert.Equal(ApplyResult.Applied, await Apply(await Msg(id, "COMPLETED", _userA)));
        Assert.Equal(ApplyResult.Duplicate, await Apply(await Msg(id, "COMPLETED", _userA)));

        var todo = await Load(id);
        Assert.Equal(ProcessingStatus.Completed, todo.ProcessingStatus);
        Assert.NotNull(todo.ProcessedAt);
        Assert.Equal("worker-1", todo.WorkerId);
    }

    [Fact]
    public async Task Late_or_out_of_order_reports_cannot_move_a_todo_backwards()
    {
        var id = await Json.CreateTodoAsync(A);
        await Apply(await Msg(id, "WAITING_RELEASE")); // PROCESSING report is missing/late: skipping forward is fine
        Assert.Equal(ProcessingStatus.WaitingRelease, (await Load(id)).ProcessingStatus);

        Assert.Equal(ApplyResult.Ignored, await Apply(await Msg(id, "PROCESSING"))); // the late one
        Assert.Equal(ProcessingStatus.WaitingRelease, (await Load(id)).ProcessingStatus);
    }

    [Fact]
    public async Task Terminal_states_are_final_for_reports()
    {
        var id = await Json.CreateTodoAsync(A);
        await Apply(await Msg(id, "COMPLETED"));
        Assert.Equal(ApplyResult.Ignored, await Apply(await Msg(id, "FAILED", error: "late failure")));
        Assert.Equal(ApplyResult.Ignored, await Apply(await Msg(id, "PROCESSING")));
        Assert.Equal(ProcessingStatus.Completed, (await Load(id)).ProcessingStatus);
    }

    [Fact]
    public async Task Failure_is_recorded_with_a_bounded_reason()
    {
        var id = await Json.CreateTodoAsync(A);
        await Apply(await Msg(id, "PROCESSING"));
        await Apply(await Msg(id, "FAILED", error: new string('x', 5000)));

        var todo = await Load(id);
        Assert.Equal(ProcessingStatus.Failed, todo.ProcessingStatus);
        Assert.Equal(200, todo.ProcessingError!.Length);
    }

    [Fact]
    public async Task Reports_for_unknown_todos_create_nothing_and_a_foreign_owner_claim_is_ignored()
    {
        var ghost = Guid.NewGuid();
        Assert.Equal(ApplyResult.NotFound, await Apply(await Msg(ghost, "PROCESSING")));
        Assert.False(await WithDb(db => db.Todos.AnyAsync(t => t.Id == ghost))); // a status message never creates a todo

        var id = await Json.CreateTodoAsync(A);
        Assert.Equal(ApplyResult.Ignored, await Apply(await Msg(id, "PROCESSING", user: _userB))); // wrong owner
        Assert.Equal(ProcessingStatus.Queued, (await Load(id)).ProcessingStatus);
    }

    [Fact]
    public async Task Invalid_status_values_are_rejected()
    {
        var id = await Json.CreateTodoAsync(A);
        await Assert.ThrowsAsync<ArgumentException>(async () => await Apply(await Msg(id, "SOMETHING_ELSE")));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Apply(await Msg(id, "QUEUED"))); // only an admin retry re-queues
    }

    [Fact]
    public async Task Concurrent_duplicate_reports_apply_exactly_once()
    {
        var id = await Json.CreateTodoAsync(A);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => await Apply(await Msg(id, "WAITING_RELEASE"))));
        Assert.Equal(1, results.Count(r => r == ApplyResult.Applied));
        Assert.Equal(7, results.Count(r => r == ApplyResult.Duplicate));
    }
}
