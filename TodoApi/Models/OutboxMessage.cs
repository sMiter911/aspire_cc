namespace TodoApi.Models;

/// <summary>
/// Transactional outbox row. It is written in the SAME database transaction as the business change, and a
/// background publisher later sends it to RabbitMQ. This closes the dual-write gap ("todo saved, publish failed").
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }
    public string Exchange { get; set; } = string.Empty;
    public string RoutingKey { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}
