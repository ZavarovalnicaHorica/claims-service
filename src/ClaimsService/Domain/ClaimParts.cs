namespace Horica.ClaimsService.Domain;

/// <summary>One check done by an agent ("Data agent" or "Recommendation agent").</summary>
public sealed record AgentStep(string Agent, StepOutcome Outcome, string Note);

/// <summary>A document attached to a claim. The file itself is kept by DocumentService.</summary>
public class ClaimDocument
{
    public Guid Id { get; set; }

    public string ClaimId { get; set; } = string.Empty;

    /// <summary>1, 2, 3 ... in the order the documents were added.</summary>
    public int Position { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Id in DocumentService, when known (from document.added.v1).</summary>
    public string? DocumentId { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public string? UploadedById { get; set; }
}

/// <summary>One entry of the handling history of a claim (UC-4, UC-8).</summary>
public class ClaimHistoryEntry
{
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>1, 2, 3 ... per claim, so entries written in the same moment keep their order.</summary>
    public int Seq { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Kafka event name where there is one (claim.submitted.v1, review.completed.v1 ...).</summary>
    public string Event { get; set; } = string.Empty;

    /// <summary>Who did it: the customer, the reviewer, or a service name.</summary>
    public string Actor { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    /// <summary>False for internal entries (agent details, risk score) that only employees see.</summary>
    public bool CustomerVisible { get; set; }
}

/// <summary>Names of history entries. They match the Kafka event names where there is one.</summary>
public static class HistoryEvents
{
    public const string Submitted = "claim.submitted.v1";
    public const string DocumentAdded = "document.added.v1";
    public const string DataCollected = "agent.data-collected";
    public const string RiskAssessed = "risk.assessed";
    public const string RecommendationCreated = "claim.recommendation.created.v1";
    public const string ReviewRequested = "review.requested.v1";
    public const string ReviewReasons = "claim.review-reasons";
    public const string ReviewCompleted = "review.completed.v1";
    public const string StatusChanged = "claim.status.changed";
    public const string ReanalysisStarted = "claim.reanalysis.started";
}

/// <summary>Limits for automatic approval (from the project plan; they may change).</summary>
public sealed class AutoApprovalOptions
{
    public const string SectionName = "AutoApproval";

    /// <summary>When false every claim goes to an employee.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Highest amount (EUR) that can be approved without an employee.</summary>
    public decimal MaxAmount { get; set; } = 300m;

    /// <summary>Highest risk score (0..1) that can be approved without an employee.</summary>
    public double MaxRiskScore { get; set; } = 0.20;

    /// <summary>At least one document must be attached.</summary>
    public bool RequireDocuments { get; set; } = true;
}
