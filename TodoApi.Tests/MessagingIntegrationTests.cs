using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using RabbitMQ.Client;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using TodoApi.Data;
using TodoApi.Models;

namespace TodoApi.Tests;

/// <summary>The API with messaging ENABLED against a real RabbitMQ loaded with the repo's definitions.json.</summary>
public sealed class MessagingApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").WithPortBinding(15672, true).Build();

    public string AmqpUri => _rabbit.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_db.StartAsync(), _rabbit.StartAsync());
        await ImportDefinitionsAsync();
        _ = Services; // start the host now so the consumers are running before any test publishes
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    public Task<ExecResult> Rabbitmqctl(params string[] args) => _rabbit.ExecAsync(["rabbitmqctl", .. args]);

    private async Task ImportDefinitionsAsync()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "messaging", "rabbitmq", "definitions.json")))
        {
            dir = dir.Parent;
        }
        var body = await File.ReadAllTextAsync(Path.Combine(dir!.FullName, "messaging", "rabbitmq", "definitions.json"));

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_rabbit.GetMappedPublicPort(15672)}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String("rabbitmq:rabbitmq"u8.ToArray()));
        var last = "no response";
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                var response = await http.PostAsync("/api/definitions", new StringContent(body, Encoding.UTF8, "application/json"));
                if (response.IsSuccessStatusCode) return;
                last = $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
            }
            catch (HttpRequestException ex) { last = ex.Message; /* management plugin still starting */ }
            await Task.Delay(1000);
        }
        throw new InvalidOperationException("Could not import RabbitMQ definitions: " + last);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("ConnectionStrings:tododb", _db.GetConnectionString());
        builder.UseSetting("ConnectionStrings:messaging", AmqpUri);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:Issuer", TodoApiFactory.Issuer);
        builder.UseSetting("Auth:Audience", TodoApiFactory.Audience);
        builder.UseSetting("Messaging:Enabled", "true");
        builder.UseSetting("Messaging:OutboxPollInterval", "00:00:00.100");

        builder.ConfigureTestServices(services =>
        {
            var config = new OpenIdConnectConfiguration { Issuer = TodoApiFactory.Issuer };
            config.SigningKeys.Add(TodoApiFactory.SigningKey);
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                o => o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config));
        });
    }

    public HttpClient ClientFor(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

[CollectionDefinition("messaging")]
public class MessagingCollection : ICollectionFixture<MessagingApiFactory>;

[Collection("messaging")]
public class MessagingIntegrationTests(MessagingApiFactory factory)
{
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    private HttpClient User => factory.ClientFor(Tokens.User(_user));
    private HttpClient Admin => factory.ClientFor(Tokens.Admin(_admin));

    private async Task<IConnection> Connect() =>
        await new ConnectionFactory { Uri = new Uri(factory.AmqpUri) }.CreateConnectionAsync();

    /// <summary>Waits for a message on a queue whose body satisfies <paramref name="match"/>; acks what it reads.</summary>
    private async Task<JsonElement> WaitForMessage(string queue, Func<JsonElement, bool> match, int seconds = 30)
    {
        await using var conn = await Connect();
        await using var channel = await conn.CreateChannelAsync();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true);
            if (result is not null)
            {
                var json = JsonDocument.Parse(result.Body.ToArray()).RootElement.Clone();
                if (match(json)) return json;
                continue;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"No matching message on {queue}");
    }

    private async Task PublishRaw(string routingKey, string json)
    {
        await using var conn = await Connect();
        await using var channel = await conn.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.BasicPublishAsync("todo.events", routingKey, mandatory: true,
            new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent, MessageId = Guid.NewGuid().ToString() },
            Encoding.UTF8.GetBytes(json));
    }

    private async Task<T> WithDb<T>(Func<TodoDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TodoDbContext>());
    }

    private async Task<Guid> CreateTodo(string title = "From the broker test")
    {
        var response = await User.PostAsJsonAsync("/api/todos", new { title });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static string Status(Guid todoId, string status, Guid? userId = null) =>
        JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(), eventType = "TodoStatusChanged", todoId, userId, status,
            workerId = "worker-test", occurredAt = DateTimeOffset.UtcNow,
        });

    private async Task WaitForStatus(Guid id, ProcessingStatus expected, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        ProcessingStatus current = default;
        while (DateTime.UtcNow < deadline)
        {
            current = await WithDb(db => db.Todos.AsNoTracking().Where(t => t.Id == id).Select(t => t.ProcessingStatus).FirstAsync());
            if (current == expected) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Todo stayed {current}, expected {expected}");
    }

    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_todo_publishes_TodoCreated_to_the_processing_queue_via_the_outbox()
    {
        var id = await CreateTodo("Learn Aspire");

        var message = await WaitForMessage("todo.processing", m => m.GetProperty("todoId").GetGuid() == id);

        Assert.Equal("TodoCreated", message.GetProperty("eventType").GetString());
        Assert.Equal(_user, message.GetProperty("userId").GetGuid());
        Assert.NotEqual(Guid.Empty, message.GetProperty("eventId").GetGuid());
        Assert.DoesNotContain("Learn Aspire", message.GetRawText()); // identifiers only

        var published = await WithDb(db => db.Outbox.AsNoTracking().Where(m => m.Payload.Contains(id.ToString())).Select(m => m.PublishedAt).FirstAsync());
        Assert.NotNull(published); // marked only after the broker confirmed it
    }

    [Fact]
    public async Task Status_reports_from_the_worker_are_consumed_and_persisted()
    {
        var id = await CreateTodo();

        await PublishRaw("todo.status.changed", Status(id, "PROCESSING", _user));
        await WaitForStatus(id, ProcessingStatus.Processing);
        await PublishRaw("todo.status.changed", Status(id, "WAITING_RELEASE", _user));
        await WaitForStatus(id, ProcessingStatus.WaitingRelease);
        await PublishRaw("todo.status.changed", Status(id, "WAITING_RELEASE", _user)); // duplicate delivery
        await PublishRaw("todo.status.changed", Status(id, "PERSISTING", _user));
        await PublishRaw("todo.status.changed", Status(id, "COMPLETED", _user));
        await WaitForStatus(id, ProcessingStatus.Completed);

        var asOwner = await User.GetFromJsonAsync<JsonElement>($"/api/todos/{id}");
        Assert.Equal("COMPLETED", asOwner.GetProperty("processingStatus").GetString());
    }

    [Fact]
    public async Task A_malformed_status_message_is_dead_lettered_not_lost_or_looped()
    {
        await PublishRaw("todo.status.changed", """{"this":"is not a status message"}""");
        await PublishRaw("todo.status.changed", Status(Guid.NewGuid(), "NOT_A_STATUS"));

        // Read both together: messages that do not match a single search would otherwise be consumed and lost.
        var seen = new List<JsonElement>();
        while (seen.Count < 2)
        {
            seen.Add(await WaitForMessage("todo.status.dlq", _ => true));
        }
        Assert.Contains(seen, m => m.TryGetProperty("this", out _));
        Assert.Contains(seen, m => m.TryGetProperty("status", out var s) && s.GetString() == "NOT_A_STATUS");
    }

    [Fact]
    public async Task Admin_release_reaches_the_worker_queue_with_the_admins_id()
    {
        var id = await CreateTodo();
        await PublishRaw("todo.status.changed", Status(id, "WAITING_RELEASE", _user));
        await WaitForStatus(id, ProcessingStatus.WaitingRelease);

        Assert.Equal(HttpStatusCode.Forbidden, (await User.PostAsync($"/api/admin/todos/{id}/release", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Admin.PostAsync($"/api/admin/todos/{id}/release", null)).StatusCode);

        var command = await WaitForMessage("todo.release", m => m.TryGetProperty("todoId", out var t) && t.ValueKind == JsonValueKind.String && t.GetGuid() == id);
        Assert.Equal("ReleaseRequested", command.GetProperty("eventType").GetString());
        Assert.Equal("single", command.GetProperty("scope").GetString());
        Assert.Equal(_admin, command.GetProperty("requestedBy").GetGuid());
    }

    [Fact]
    public async Task A_database_failure_while_persisting_a_status_report_requeues_it_instead_of_losing_it()
    {
        var id = await CreateTodo("db outage");

        // Simulate PostgreSQL trouble: the table the consumer writes to disappears for a few seconds.
        await WithDb(async db => await db.Database.ExecuteSqlRawAsync("ALTER TABLE todos RENAME TO todos_unavailable"));
        try
        {
            await PublishRaw("todo.status.changed", Status(id, "PROCESSING", _user));
            await Task.Delay(4000); // several failed attempts: the consumer nacks with requeue, never acks
        }
        finally
        {
            await WithDb(async db => await db.Database.ExecuteSqlRawAsync("ALTER TABLE todos_unavailable RENAME TO todos"));
        }

        // The very same message is redelivered and applied once the database is back; nothing went to the DLQ.
        await WaitForStatus(id, ProcessingStatus.Processing, seconds: 30);
        await using var conn = await Connect();
        await using var channel = await conn.CreateChannelAsync();
        var dead = await channel.BasicGetAsync("todo.status.dlq", autoAck: true);
        Assert.True(dead is null || !Encoding.UTF8.GetString(dead.Body.ToArray()).Contains(id.ToString()),
            "the report must be retried, not dead-lettered");
    }

    [Fact]
    public async Task While_RabbitMQ_is_down_todos_are_still_accepted_and_the_messages_are_delivered_after_recovery()
    {
        await CreateTodo("warm-up"); // make sure the publisher has a live connection first
        await WaitForMessage("todo.processing", _ => true);

        Assert.True((await factory.Rabbitmqctl("stop_app")).ExitCode == 0);
        Guid id;
        try
        {
            id = await CreateTodo("created during the outage");           // the API does not fail: the outbox holds it
            await Task.Delay(2000);
            var pending = await WithDb(db => db.Outbox.AsNoTracking().Where(m => m.Payload.Contains(id.ToString())).Select(m => m.PublishedAt).FirstAsync());
            Assert.Null(pending);                                         // nothing was published, nothing was lost

            var health = await factory.CreateClient().GetStringAsync("/api/health");
            Assert.Contains(health, new[] { "Healthy", "Degraded" });     // predictable, not a crash
        }
        finally
        {
            Assert.True((await factory.Rabbitmqctl("start_app")).ExitCode == 0);
        }

        // after recovery the same message is published and consumable
        var message = await WaitForMessage("todo.processing", m => m.GetProperty("todoId").GetGuid() == id, seconds: 90);
        Assert.Equal("TodoCreated", message.GetProperty("eventType").GetString());
    }
}
