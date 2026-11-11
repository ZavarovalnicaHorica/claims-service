using System.ComponentModel.DataAnnotations;
using Horica.ClaimsService.Domain;

namespace Horica.ClaimsService.Application;

// The response shapes follow the web UI (web-ui/src/core/models.ts: Claim, HistoryEvent, AuditEntry),
// so the UI can switch from mock data to this service without changes.

public sealed record ClaimDto(
    string Id,
    string PolicyNumber,
    string CustomerId,
    string Claimant,
    string? Type,
    DateOnly IncidentDate,
    decimal Amount,
    string Currency,
    string Description,
    IReadOnlyList<ClaimDocumentDto> Documents,
    ClaimStatus Status,
    RiskDto Risk,
    IReadOnlyList<AgentStep> Agents,
    RecommendationDto Recommendation,
    int AnalysisRound,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    ReviewDto? Review,
    IReadOnlyList<HistoryEventDto> History,
    int Version);

public sealed record ClaimDocumentDto(string Name, DateTimeOffset UploadedAt, string UploadedById, string? DocumentId);

/// <summary>score is 0 and assessed false until RiskService has scored the claim.</summary>
public sealed record RiskDto(double Score, IReadOnlyList<string> Reasons, bool Assessed);

/// <summary>action is REVIEW and summary empty until the agents have made a proposal.</summary>
public sealed record RecommendationDto(RecommendationAction Action, string Summary, bool Automatic);

/// <summary>Latest employee decision. A NEEDS_INFO decision stays until the claim is decided again.</summary>
public sealed record ReviewDto(
    ReviewDecision Decision,
    string ReviewerId,
    string ReviewerName,
    string Comment,
    DateTimeOffset DecidedAt,
    string? ReviewId);

/// <summary>One entry of a claim's history. "event" is the Kafka event name where there is one.</summary>
public sealed record HistoryEventDto(DateTimeOffset At, string Event, string Actor, string Text);

/// <summary>A history entry together with its claim, for the audit log (UC-8).</summary>
public sealed record AuditEntryDto(string ClaimId, DateTimeOffset At, string Event, string Actor, string Text);

/// <summary>Body of POST /claims (same as NewClaim in the web UI).</summary>
public sealed class SubmitClaimRequest
{
    /// <summary>Number of one of the customer's policies, e.g. POL-1001.</summary>
    [Required]
    [MaxLength(100)]
    public string? PolicyNumber { get; set; }

    /// <summary>Day of the incident (yyyy-MM-dd), not in the future.</summary>
    [Required]
    public DateOnly? IncidentDate { get; set; }

    /// <summary>Amount claimed in EUR, more than 0.</summary>
    [Required]
    public decimal? Amount { get; set; }

    /// <summary>What happened.</summary>
    [Required]
    [MaxLength(4000)]
    public string? Description { get; set; }

    /// <summary>Names of the attached files (the files themselves go to DocumentService).</summary>
    public List<string>? Documents { get; set; }

    /// <summary>Optional: CAR, HOME, TRAVEL or HEALTH. Taken from PolicyService when it is configured.</summary>
    [MaxLength(30)]
    public string? Type { get; set; }
}

/// <summary>Query string of GET /claims.</summary>
public sealed class ClaimListQuery
{
    /// <summary>SUBMITTED, NEEDS_REVIEW, NEEDS_INFO, APPROVED or REJECTED. Omit for all.</summary>
    public string? Status { get; set; }

    /// <summary>Employees only: claims of one customer.</summary>
    public string? CustomerId { get; set; }

    public string? PolicyNumber { get; set; }

    /// <summary>Newest first; at most this many (1..1000, default 500).</summary>
    public int Limit { get; set; } = 500;
}

/// <summary>Query string of GET /claims/history.</summary>
public sealed class AuditQuery
{
    /// <summary>Only entries of this claim.</summary>
    public string? ClaimId { get; set; }

    /// <summary>Only entries with this event name, e.g. review.completed.v1.</summary>
    public string? Event { get; set; }

    /// <summary>Newest first; at most this many (1..5000, default 1000).</summary>
    public int Limit { get; set; } = 1000;
}

public static class ClaimMappings
{
    public static ClaimDto ToDto(this Claim claim, bool staffView)
    {
        var history = claim.History
            .Where(h => staffView || h.CustomerVisible)
            .OrderBy(h => h.Seq)
            .Select(h => h.ToDto())
            .ToList();

        var documents = claim.Documents
            .OrderBy(d => d.Position)
            .Select(d => new ClaimDocumentDto(d.Name, d.UploadedAt, d.UploadedById ?? string.Empty, d.DocumentId))
            .ToList();

        ReviewDto? review = null;
        if (claim.ReviewDecision.HasValue)
        {
            review = new ReviewDto(
                Decision: claim.ReviewDecision.Value,
                ReviewerId: claim.ReviewerId ?? string.Empty,
                ReviewerName: claim.ReviewerName ?? claim.ReviewerId ?? "Reviewer",
                Comment: claim.ReviewComment ?? string.Empty,
                DecidedAt: claim.ReviewedAt ?? claim.UpdatedAt,
                ReviewId: claim.ReviewId);
        }

        return new ClaimDto(
            Id: claim.Id,
            PolicyNumber: claim.PolicyNumber,
            CustomerId: claim.CustomerId,
            Claimant: claim.Claimant,
            Type: claim.Type,
            IncidentDate: claim.IncidentDate,
            Amount: claim.Amount,
            Currency: claim.Currency,
            Description: claim.Description,
            Documents: documents,
            Status: claim.Status,
            Risk: new RiskDto(claim.RiskScore ?? 0.0, claim.RiskReasons.ToList(), claim.RiskScore.HasValue),
            Agents: claim.Agents.ToList(),
            Recommendation: new RecommendationDto(
                claim.RecommendationAction ?? RecommendationAction.Review,
                claim.RecommendationSummary ?? string.Empty,
                claim.RecommendationAutomatic),
            AnalysisRound: claim.AnalysisRound,
            CreatedAt: claim.CreatedAt,
            UpdatedAt: claim.UpdatedAt,
            ClosedAt: claim.ClosedAt,
            Review: review,
            History: history,
            Version: claim.Version);
    }

    public static HistoryEventDto ToDto(this ClaimHistoryEntry entry)
    {
        return new HistoryEventDto(entry.At, entry.Event, entry.Actor, entry.Text);
    }
}
