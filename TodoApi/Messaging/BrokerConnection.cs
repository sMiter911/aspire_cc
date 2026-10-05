using RabbitMQ.Client;

namespace TodoApi.Messaging;

/// <summary>
/// Resolves the shared RabbitMQ connection lazily and retries while the broker is unavailable, so the API starts
/// (and keeps accepting todos through the outbox) even if RabbitMQ is down. Once connected, the client library's
/// automatic recovery re-establishes the connection and its consumers after later outages.
/// </summary>
public sealed class BrokerConnection(IServiceProvider services, ILogger<BrokerConnection> log)
{
    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                return services.GetRequiredService<IConnection>();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("RabbitMQ unavailable ({Reason}); retrying in {Delay}s", ex.GetType().Name, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
    }
}
