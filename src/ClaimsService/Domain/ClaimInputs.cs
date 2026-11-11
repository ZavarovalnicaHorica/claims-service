namespace Horica.ClaimsService.Domain;

/// <summary>A new claim as submitted by a customer (already validated).</summary>
public sealed record NewClaimData(
    string Id,
    string CustomerId,
    string Claimant,
    string PolicyNumber,
    string? Type,
    DateOnly IncidentDate,
    decimal Amount,
    string Currency,
    string Description,
    IReadOnlyList<string> Documents);

/// <summary>Data of claim.recommendation.created.v1 (from AgentService).</summary>
public sealed record RecommendationInput(
    string ClaimId,
    RecommendationAction Action,
    string? Summary,
    double? RiskScore,
    IReadOnlyList<string> RiskReasons,
    IReadOnlyList<AgentStep> Agents,
    bool? PolicyValid,
    bool? DocumentsComplete,
    int? AnalysisRound);

/// <summary>Data of review.completed.v1 (from ReviewService).</summary>
public sealed record ReviewResultInput(
    string ClaimId,
    ReviewDecision Decision,
    string? Reason,
    string? ReviewId,
    string? ReviewerId,
    string? ReviewerName,
    DateTimeOffset? DecidedAt);

public sealed record IncomingDocument(string Name, string? DocumentId);

/// <summary>Data of document.added.v1 (from DocumentService).</summary>
public sealed record DocumentAddedInput(
    string ClaimId,
    IReadOnlyList<IncomingDocument> Documents,
    string? UploadedById,
    DateTimeOffset? UploadedAt);

/// <summary>What happened when an incoming event was applied to a claim.</summary>
public sealed record ApplyResult(bool Applied, string Note)
{
    public static ApplyResult Done(string note) => new ApplyResult(true, note);

    public static ApplyResult Ignored(string note) => new ApplyResult(false, note);
}
