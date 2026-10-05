using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TodoApi.Data;
using TodoApi.Models;

namespace TodoApi.Messaging;

public static class OutboxExtensions
{
    /// <summary>
    /// Adds a message to the outbox. It is saved by the caller's next <c>SaveChanges</c>, i.e. in the same
    /// transaction as the business change it describes: either both exist or neither does.
    /// </summary>
    public static void AddOutbox<T>(this TodoDbContext db, string routingKey, string eventType, T message, DateTimeOffset now) =>
        db.Outbox.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Exchange = Topology.Exchange,
            RoutingKey = routingKey,
            EventType = eventType,
            Payload = MessageJson.Serialize(message),
            CreatedAt = now,
        });
}

/// <summary>
/// Publishes outbox rows to RabbitMQ. At-least-once: a row is marked published only after the broker confirmed it,
/// so a crash between publish and commit re-sends it (consumers are idempotent). If the broker is down the rows
/// simply wait; nothing is lost and the API keeps accepting requests.
/// </summary>
public sealed class OutboxPublisher(
    IServiceScopeFactory scopes,
    BrokerConnection broker,
    IOptions<MessagingOptions> options,
    TimeProvider time,
    ILogger<OutboxPublisher> log) : BackgroundService
{
    private readonly MessagingOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await PublishBatchAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(1);
                if (sent == 0)
                {
                    await Task.Delay(_opts.OutboxPollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Broker or database trouble: keep the rows, back off, try again. Never crash the host.
                log.LogWarning("Outbox publish failed ({Reason}); retrying in {Delay}s", ex.GetType().Name, backoff.TotalSeconds);
                try { await Task.Delay(backoff, stoppingToken); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    internal async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        // A manual transaction must run inside the retrying execution strategy the Npgsql integration enables.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // SKIP LOCKED lets several API instances share the outbox without publishing the same row at once.
            var batch = await db.Outbox
                .FromSqlRaw("SELECT * FROM outbox_messages WHERE published_at IS NULL ORDER BY created_at LIMIT {0} FOR UPDATE SKIP LOCKED",
                    _opts.OutboxBatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                return 0;
            }

            var connection = await broker.GetAsync(ct);
            await using var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                ct);

            foreach (var message in batch)
            {
                var props = new BasicProperties
                {
                    ContentType = "application/json",
                    DeliveryMode = DeliveryModes.Persistent,
                    MessageId = message.Id.ToString(),
                    Type = message.EventType,
                    Timestamp = new AmqpTimestamp(message.CreatedAt.ToUnixTimeSeconds()),
                };
                // mandatory + confirm tracking: awaits the broker's confirm and throws if the message is unroutable.
                await channel.BasicPublishAsync(message.Exchange, message.RoutingKey, mandatory: true, props,
                    System.Text.Encoding.UTF8.GetBytes(message.Payload), ct);
                message.PublishedAt = time.GetUtcNow();
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            log.LogDebug("Published {Count} outbox message(s)", batch.Count);
            return batch.Count;
        });
    }
}

/// <summary>Reports Degraded (still HTTP 200) when the outbox is backing up, e.g. while RabbitMQ is down.</summary>
public sealed class OutboxHealthCheck(IServiceScopeFactory scopes, IOptions<MessagingOptions> options, TimeProvider time) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        var oldest = await db.Outbox.Where(m => m.PublishedAt == null)
            .OrderBy(m => m.CreatedAt).Select(m => (DateTimeOffset?)m.CreatedAt).FirstOrDefaultAsync(ct);
        return oldest is { } t && time.GetUtcNow() - t > options.Value.OutboxDegradedAfter
            ? HealthCheckResult.Degraded("Outbox is not draining")
            : HealthCheckResult.Healthy();
    }
}
