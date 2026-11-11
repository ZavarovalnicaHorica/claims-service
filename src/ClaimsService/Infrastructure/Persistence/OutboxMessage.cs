namespace Horica.ClaimsService.Infrastructure.Persistence;

/// <summary>
/// Event waiting to be published to Kafka. It is written in the same database transaction as the
/// change of the claim, so a status change can never be saved without its event.
/// </summary>
public class OutboxMessage
{
    /// <summary>Also used as the CloudEvents id, so consumers can de-duplicate.</summary>
    public Guid Id { get; set; }

    public string Topic { get; set; } = string.Empty;

    public string MessageKey { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    /// <summary>The complete CloudEvents JSON document.</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Order of the events written in the same transaction (they share CreatedAt).</summary>
    public int Sequence { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
