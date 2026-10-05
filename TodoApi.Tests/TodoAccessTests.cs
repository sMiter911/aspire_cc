using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TodoApi.Tests;

/// <summary>USER and ADMIN behavior: own todos, IDOR protection, cross-user admin powers.</summary>
[Collection("api")]
public class TodoAccessTests(TodoApiFactory factory)
{
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    private HttpClient A => factory.ClientFor(Tokens.User(_userA));
    private HttpClient B => factory.ClientFor(Tokens.User(_userB));
    private HttpClient Admin => factory.ClientFor(Tokens.Admin(_admin));

    // ---- USER: own todos ----------------------------------------------------------------------------

    [Fact]
    public async Task User_can_create_read_update_complete_and_delete_own_todo()
    {
        var created = await A.PostAsJsonAsync("/api/todos", new { title = "Write tests", description = "d" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var todo = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = todo.GetProperty("id").GetGuid();
        Assert.Equal(_userA, todo.GetProperty("userId").GetGuid()); // owner comes from the token

        Assert.Equal(HttpStatusCode.OK, (await A.GetAsync($"/api/todos/{id}")).StatusCode);

        var put = await A.PutAsJsonAsync($"/api/todos/{id}", new { title = "Renamed", description = (string?)null, isCompleted = false });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Renamed", (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());

        var patch = await A.PatchAsync($"/api/todos/{id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.True((await patch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isCompleted").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await A.DeleteAsync($"/api/todos/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await A.GetAsync($"/api/todos/{id}")).StatusCode);
    }

    [Fact]
    public async Task Listing_returns_only_the_callers_todos()
    {
        var mine = await Json.CreateTodoAsync(A, "mine");
        var theirs = await Json.CreateTodoAsync(B, "theirs");

        var page = await A.GetFromJsonAsync<JsonElement>("/api/todos?size=100");
        var ids = page.GetProperty("content").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToList();

        Assert.Contains(mine, ids);
        Assert.DoesNotContain(theirs, ids);
        Assert.All(page.GetProperty("content").EnumerateArray(),
            t => Assert.Equal(_userA, t.GetProperty("userId").GetGuid()));
    }

    [Fact]
    public async Task Validation_errors_use_the_shared_error_envelope()
    {
        var response = await A.PostAsJsonAsync("/api/todos", new { title = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
        Assert.Equal("/api/todos", body.GetProperty("path").GetString());
        Assert.NotEmpty(body.GetProperty("errors").EnumerateArray());
    }

    // ---- IDOR ---------------------------------------------------------------------------------------

    [Fact]
    public async Task User_cannot_read_update_complete_or_delete_another_users_todo()
    {
        var bobsTodo = await Json.CreateTodoAsync(B, "private");

        Assert.Equal(HttpStatusCode.NotFound, (await A.GetAsync($"/api/todos/{bobsTodo}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await A.PutAsJsonAsync($"/api/todos/{bobsTodo}", new { title = "pwned", isCompleted = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await A.PatchAsync($"/api/todos/{bobsTodo}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await A.DeleteAsync($"/api/todos/{bobsTodo}")).StatusCode);

        // Nothing changed, and the owner still has it.
        var intact = await B.GetFromJsonAsync<JsonElement>($"/api/todos/{bobsTodo}");
        Assert.Equal("private", intact.GetProperty("title").GetString());
        Assert.False(intact.GetProperty("isCompleted").GetBoolean());
    }

    [Fact]
    public async Task Foreign_and_nonexistent_ids_are_indistinguishable()
    {
        var bobsTodo = await Json.CreateTodoAsync(B);
        var foreign = await A.GetAsync($"/api/todos/{bobsTodo}");
        var missing = await A.GetAsync($"/api/todos/{Guid.NewGuid()}");

        Assert.Equal(foreign.StatusCode, missing.StatusCode);
        var f = await foreign.Content.ReadFromJsonAsync<JsonElement>();
        var m = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(f.GetProperty("code").GetString(), m.GetProperty("code").GetString());
        Assert.Equal(f.GetProperty("message").GetString(), m.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Client_cannot_choose_the_owner_of_a_todo()
    {
        var response = await A.PostAsync("/api/todos",
            Json.Body($$"""{"title":"sneaky","userId":"{{_userB}}"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // unknown member rejected outright

        var bobsList = await B.GetFromJsonAsync<JsonElement>("/api/todos?size=100");
        Assert.DoesNotContain(bobsList.GetProperty("content").EnumerateArray(),
            t => t.GetProperty("title").GetString() == "sneaky");
    }

    [Fact]
    public async Task Update_cannot_reassign_ownership()
    {
        var id = await Json.CreateTodoAsync(A);
        var response = await A.PutAsync($"/api/todos/{id}",
            Json.Body($$"""{"title":"t","isCompleted":false,"userId":"{{_userB}}"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(_userA, (await A.GetFromJsonAsync<JsonElement>($"/api/todos/{id}")).GetProperty("userId").GetGuid());
    }

    // ---- USER vs admin surface ---------------------------------------------------------------------

    [Fact]
    public async Task User_cannot_list_all_todos_or_use_admin_endpoint()
    {
        var response = await A.GetAsync("/api/admin/todos");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACCESS_DENIED", body.GetProperty("code").GetString());
    }

    // ---- ADMIN ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_can_create_and_read_own_todo()
    {
        var id = await Json.CreateTodoAsync(Admin, "admin own");
        var todo = await Admin.GetFromJsonAsync<JsonElement>($"/api/todos/{id}");
        Assert.Equal(_admin, todo.GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task Admin_can_read_update_and_delete_any_users_todo()
    {
        var id = await Json.CreateTodoAsync(A, "users todo");

        var read = await Admin.GetAsync($"/api/todos/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var put = await Admin.PutAsJsonAsync($"/api/todos/{id}", new { title = "edited by admin", isCompleted = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_userA, updated.GetProperty("userId").GetGuid()); // still the original owner

        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/todos/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await A.GetAsync($"/api/todos/{id}")).StatusCode);
    }

    [Fact]
    public async Task Admin_can_list_all_todos_and_filter_by_user()
    {
        var a = await Json.CreateTodoAsync(A, "a-todo");
        var b = await Json.CreateTodoAsync(B, "b-todo");

        var all = await Admin.GetFromJsonAsync<JsonElement>("/api/admin/todos?size=100");
        var allIds = all.GetProperty("content").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(a, allIds);
        Assert.Contains(b, allIds);

        var onlyB = await Admin.GetFromJsonAsync<JsonElement>($"/api/admin/todos?userId={_userB}&size=100");
        Assert.All(onlyB.GetProperty("content").EnumerateArray(),
            t => Assert.Equal(_userB, t.GetProperty("userId").GetGuid()));
        Assert.Contains(b, onlyB.GetProperty("content").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()));
    }

    // ---- misc ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Paging_is_bounded()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await A.GetAsync("/api/todos?size=100000")).StatusCode);
    }

    [Fact]
    public async Task Health_is_public_and_reveals_no_details()
    {
        var response = await factory.ClientFor().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
