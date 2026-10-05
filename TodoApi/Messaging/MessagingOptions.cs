namespace TodoApi.Messaging;

public class MessagingOptions
{
    public const string Section = "Messaging";

    /// <summary>
    /// When false the API runs without a broker: outbox rows are still written (tests, offline development) but
    /// nothing is published and nothing is consumed.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public int OutboxBatchSize { get; set; } = 50;
    public ushort ConsumerPrefetch { get; set; } = 10;

    /// <summary>The outbox reports Degraded in /api/health when its oldest unpublished message is older than this.</summary>
    public TimeSpan OutboxDegradedAfter { get; set; } = TimeSpan.FromMinutes(2);
}
