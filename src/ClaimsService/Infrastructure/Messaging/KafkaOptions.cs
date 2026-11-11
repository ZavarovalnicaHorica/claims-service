namespace Horica.ClaimsService.Infrastructure.Messaging;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    /// <summary>Set to false to run the API without Kafka (events are still written to the outbox table).</summary>
    public bool Enabled { get; set; } = true;

    public string BootstrapServers { get; set; } = "localhost:9092";

    public string ClientId { get; set; } = "claims-service";

    public string ConsumerGroupId { get; set; } = "claims-service";

    // Published by this service.

    /// <summary>A claim was submitted (or re-submitted after new documents): the agents should analyse it.</summary>
    public string ClaimSubmittedTopic { get; set; } = "claim.submitted.v1";

    /// <summary>The status of a claim changed.</summary>
    public string ClaimStatusChangedTopic { get; set; } = "claim.status.changed.v1";

    /// <summary>An employee has to decide (consumed by ReviewService).</summary>
    public string ReviewRequestedTopic { get; set; } = "review.requested.v1";

    // Consumed by this service.

    /// <summary>The agents' proposal (published by AgentService).</summary>
    public string RecommendationCreatedTopic { get; set; } = "claim.recommendation.created.v1";

    /// <summary>The employee's decision (published by ReviewService).</summary>
    public string ReviewCompletedTopic { get; set; } = "review.completed.v1";

    /// <summary>A document was added to a claim (published by DocumentService).</summary>
    public string DocumentAddedTopic { get; set; } = "document.added.v1";

    /// <summary>Create the topics on startup if they do not exist.</summary>
    public bool EnsureTopics { get; set; } = true;

    public int TopicPartitions { get; set; } = 3;

    public short TopicReplicationFactor { get; set; } = 1;

    public int OutboxPollIntervalMs { get; set; } = 1000;

    public int OutboxBatchSize { get; set; } = 50;

    // Optional security settings (e.g. for a secured Strimzi listener or a managed Kafka).
    // SecurityProtocol: Plaintext | Ssl | SaslPlaintext | SaslSsl
    // SaslMechanism: Plain | ScramSha256 | ScramSha512 | OAuthBearer | Gssapi
    public string? SecurityProtocol { get; set; }

    public string? SaslMechanism { get; set; }

    public string? SaslUsername { get; set; }

    public string? SaslPassword { get; set; }

    public IReadOnlyList<string> ConsumedTopics =>
        new[] { RecommendationCreatedTopic, ReviewCompletedTopic, DocumentAddedTopic };

    public IReadOnlyList<string> PublishedTopics =>
        new[] { ClaimSubmittedTopic, ClaimStatusChangedTopic, ReviewRequestedTopic };

    /// <summary>Messages that cannot be parsed are copied to "&lt;topic&gt;.dlq" instead of blocking the partition.</summary>
    public static string DeadLetterTopicFor(string topic) => topic + ".dlq";
}
