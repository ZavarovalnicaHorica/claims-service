using System.Text.Json;
using Horica.ClaimsService.Domain;
using Horica.ClaimsService.Infrastructure.Persistence;

namespace Horica.ClaimsService.Infrastructure.Messaging;

/// <summary>Data of claim.submitted.v1: everything the agents need to start.</summary>
public sealed record ClaimSubmittedData(
    string ClaimId,
    string PolicyNumber,
    string PolicyId,
    string CustomerId,
    string Claimant,
    string? Type,
    DateOnly IncidentDate,
    decimal ClaimedAmount,
    string Currency,
    string Description,
    IReadOnlyList<string> Documents,
    int AnalysisRound,
    bool Reanalysis,
    DateTimeOffset SubmittedAt);

/// <summary>
/// Data of review.requested.v1, in the shape ReviewService reads (claimId, policyId, customerId, claimedAmount,
/// currency, riskScore, riskReasons, recommendation, summary). riskReasons also lists why the claim was not
/// approved automatically; reviewReasons has only those.
/// </summary>
public sealed record ReviewRequestedData(
    string ClaimId,
    string PolicyId,
    string PolicyNumber,
    string CustomerId,
    decimal ClaimedAmount,
    string Currency,
    double? RiskScore,
    IReadOnlyList<string> RiskReasons,
    IReadOnlyList<string> ReviewReasons,
    string? Recommendation,
    string? Summary,
    int AnalysisRound);

/// <summary>Data of claim.status.changed.v1.</summary>
public sealed record ClaimStatusChangedData(
    string ClaimId,
    string CustomerId,
    ClaimStatus Status,
    ClaimStatus PreviousStatus,
    string? Reason,
    int AnalysisRound,
    DateTimeOffset ChangedAt);

public static class ClaimEvents
{
    public const string SubmittedType = "claim.submitted.v1";

    public const string ReviewRequestedType = "review.requested.v1";

    public const string StatusChangedType = "claim.status.changed.v1";

    public const string RecommendationCreatedType = "claim.recommendation.created.v1";

    public const string ReviewCompletedType = "review.completed.v1";

    public const string DocumentAddedType = "document.added.v1";

    /// <summary>Turns the events the claim collected during this change into outbox rows, in order.</summary>
    public static List<OutboxMessage> ToOutbox(Claim claim, KafkaOptions kafka, string source, DateTimeOffset now)
    {
        var messages = new List<OutboxMessage>();

        foreach (var domainEvent in claim.NewEvents)
        {
            var message = domainEvent switch
            {
                ClaimSubmittedEvent submitted => Build(
                    claim, kafka.ClaimSubmittedTopic, SubmittedType, source, now, Submitted(claim, submitted, now)),
                ReviewRequestedEvent review => Build(
                    claim, kafka.ReviewRequestedTopic, ReviewRequestedType, source, now, ReviewRequested(claim, review)),
                ClaimStatusChangedEvent changed => Build(
                    claim, kafka.ClaimStatusChangedTopic, StatusChangedType, source, now, StatusChanged(claim, changed, now)),
                _ => throw new InvalidOperationException("Unknown domain event " + domainEvent.GetType().Name + ".")
            };

            message.Sequence = messages.Count;
            messages.Add(message);
        }

        return messages;
    }

    private static ClaimSubmittedData Submitted(Claim claim, ClaimSubmittedEvent submitted, DateTimeOffset now)
    {
        return new ClaimSubmittedData(
            ClaimId: claim.Id,
            PolicyNumber: claim.PolicyNumber,
            PolicyId: claim.PolicyNumber,
            CustomerId: claim.CustomerId,
            Claimant: claim.Claimant,
            Type: claim.Type,
            IncidentDate: claim.IncidentDate,
            ClaimedAmount: claim.Amount,
            Currency: claim.Currency,
            Description: claim.Description,
            Documents: claim.Documents.OrderBy(d => d.Position).Select(d => d.Name).ToList(),
            AnalysisRound: claim.AnalysisRound,
            Reanalysis: submitted.Reanalysis,
            SubmittedAt: now);
    }

    private static ReviewRequestedData ReviewRequested(Claim claim, ReviewRequestedEvent review)
    {
        return new ReviewRequestedData(
            ClaimId: claim.Id,
            PolicyId: claim.PolicyNumber,
            PolicyNumber: claim.PolicyNumber,
            CustomerId: claim.CustomerId,
            ClaimedAmount: claim.Amount,
            Currency: claim.Currency,
            RiskScore: claim.RiskScore,
            RiskReasons: claim.RiskReasons.Concat(review.Reasons).Distinct().ToList(),
            ReviewReasons: review.Reasons,
            Recommendation: claim.RecommendationAction?.ToString().ToUpperInvariant(),
            Summary: claim.RecommendationSummary,
            AnalysisRound: claim.AnalysisRound);
    }

    private static ClaimStatusChangedData StatusChanged(Claim claim, ClaimStatusChangedEvent changed, DateTimeOffset now)
    {
        return new ClaimStatusChangedData(
            ClaimId: claim.Id,
            CustomerId: claim.CustomerId,
            Status: changed.Current,
            PreviousStatus: changed.Previous,
            Reason: changed.Reason,
            AnalysisRound: claim.AnalysisRound,
            ChangedAt: now);
    }

    private static OutboxMessage Build<TData>(
        Claim claim,
        string topic,
        string type,
        string source,
        DateTimeOffset now,
        TData data)
    {
        var eventId = Guid.NewGuid();

        var envelope = new CloudEvent<TData>
        {
            Id = eventId.ToString(),
            Source = source,
            Type = type,
            Subject = "claims/" + claim.Id,
            Time = now,
            Data = data
        };

        return new OutboxMessage
        {
            Id = eventId,
            Topic = topic,
            MessageKey = claim.Id,
            EventType = type,
            Payload = JsonSerializer.Serialize(envelope, JsonDefaults.Options),
            CreatedAt = now
        };
    }
}
