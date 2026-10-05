using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TodoApi.Services;

namespace TodoApi.Messaging;

/// <summary>
/// Consumes the worker's TodoStatusChanged reports and persists them through <see cref="TodoProcessingService"/>.
/// Manual acknowledgements: a message is acked only after the database accepted (or deliberately ignored) it.
/// Database failure => requeue (the work is retried, not lost). Malformed => dead-letter queue.
/// </summary>
public sealed class StatusConsumer(
    BrokerConnection broker,
    IServiceScopeFactory scopes,
    IOptions<MessagingOptions> options,
    ILogger<StatusConsumer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await broker.GetAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await channel.BasicQosAsync(0, options.Value.ConsumerPrefetch, false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, ea) => HandleAsync(channel, ea, stoppingToken);
                await channel.BasicConsumeAsync(Topology.QueueStatus, autoAck: false, consumer, stoppingToken);
                log.LogInformation("Consuming {Queue}", Topology.QueueStatus);

                // Stay until shutdown, or until the channel is gone for good (automatic recovery handles blips).
                var closedChecks = 0;
                while (!stoppingToken.IsCancellationRequested && closedChecks < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    closedChecks = channel.IsOpen ? 0 : closedChecks + 1;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning("Status consumer failed ({Reason}); restarting in 5s", ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        StatusChangedMessage? message;
        try
        {
            message = MessageJson.Deserialize<StatusChangedMessage>(ea.Body.Span);
        }
        catch (Exception)
        {
            message = null;
        }

        if (message is null || message.EventType != EventTypes.StatusChanged || message.TodoId == Guid.Empty)
        {
            log.LogError("Malformed status message {MessageId}; dead-lettering", ea.BasicProperties.MessageId);
            await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false, ct);
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<TodoProcessingService>().ApplyStatusAsync(message, ct);
            log.LogDebug("Status message {EventId} for {TodoId}: {Result}", message.EventId, message.TodoId, result);
            await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
        }
        catch (ArgumentException)
        {
            log.LogError("Status message {EventId} has an invalid status; dead-lettering", message.EventId);
            await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Database trouble: give the message back and slow down so we do not spin.
            log.LogWarning("Could not persist status {EventId} ({Reason}); requeueing", message.EventId, ex.GetType().Name);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: true, ct);
        }
    }
}
