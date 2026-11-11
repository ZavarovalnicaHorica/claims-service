namespace Horica.ClaimsService.Infrastructure.Persistence;

/// <summary>Ids of already handled incoming events (Kafka delivers at least once).</summary>
public class ProcessedEvent
{
    public string EventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string? ClaimId { get; set; }

    /// <summary>What was done (or why it was ignored), for troubleshooting.</summary>
    public string? Outcome { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}
