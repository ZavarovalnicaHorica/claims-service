using Horica.ClaimsService.Common;

namespace Horica.ClaimsService.Domain;

/// <summary>
/// A damage claim. ClaimsService owns its final state (project plan, service 1).
/// All state changes go through the methods below so the rules live in one place.
/// Every change writes history entries (<see cref="NewHistory"/>) and, where other services must know,
/// domain events (<see cref="NewEvents"/>) that the application stores in the same transaction.
///
///   SUBMITTED --recommendation, rules ok--> APPROVED
///   SUBMITTED --recommendation, otherwise--> NEEDS_REVIEW --review--> APPROVED | REJECTED | NEEDS_INFO
///   NEEDS_INFO --new document--> SUBMITTED (re-analysis, UC-7)
/// </summary>
public class Claim
{
    public const string ServiceActor = "ClaimsService";

    // Used by EF Core.
    private Claim()
    {
    }

    /// <summary>"CLM-1001", "CLM-1002" ...</summary>
    public string Id { get; private set; } = string.Empty;

    /// <summary>"sub" of the customer's token.</summary>
    public string CustomerId { get; private set; } = string.Empty;

    /// <summary>Display name of the customer.</summary>
    public string Claimant { get; private set; } = string.Empty;

    public string PolicyNumber { get; private set; } = string.Empty;

    /// <summary>CAR, HOME, TRAVEL, HEALTH (from PolicyService or the request); null when unknown.</summary>
    public string? Type { get; private set; }

    public DateOnly IncidentDate { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "EUR";

    public string Description { get; private set; } = string.Empty;

    public ClaimStatus Status { get; private set; }

    /// <summary>1 for the first analysis, +1 for every re-analysis after new documents (UC-7).</summary>
    public int AnalysisRound { get; private set; }

    // Latest result of the agents and RiskService.
    public double? RiskScore { get; private set; }

    public List<string> RiskReasons { get; private set; } = new List<string>();

    public List<AgentStep> Agents { get; private set; } = new List<AgentStep>();

    public RecommendationAction? RecommendationAction { get; private set; }

    public string? RecommendationSummary { get; private set; }

    /// <summary>True when the claim was approved by the rules, without an employee.</summary>
    public bool RecommendationAutomatic { get; private set; }

    public DateTimeOffset? AnalyzedAt { get; private set; }

    // Latest decision of an employee (from ReviewService).
    public string? ReviewId { get; private set; }

    public ReviewDecision? ReviewDecision { get; private set; }

    public string? ReviewComment { get; private set; }

    public string? ReviewerId { get; private set; }

    public string? ReviewerName { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    /// <summary>When the claim reached APPROVED or REJECTED.</summary>
    public DateTimeOffset? ClosedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Number of history entries; the next entry gets HistoryCount + 1 as its Seq.</summary>
    public int HistoryCount { get; private set; }

    /// <summary>Optimistic concurrency token, incremented on every change.</summary>
    public int Version { get; private set; }

    public List<ClaimDocument> Documents { get; private set; } = new List<ClaimDocument>();

    /// <summary>Loaded only for reading. New entries are in <see cref="NewHistory"/>.</summary>
    public List<ClaimHistoryEntry> History { get; private set; } = new List<ClaimHistoryEntry>();

    /// <summary>History entries written by this change (not mapped; the application saves them).</summary>
    public List<ClaimHistoryEntry> NewHistory { get; } = new List<ClaimHistoryEntry>();

    /// <summary>Documents added by this change (not mapped; also in <see cref="Documents"/>).</summary>
    public List<ClaimDocument> NewDocuments { get; } = new List<ClaimDocument>();

    /// <summary>Events produced by this change (not mapped; the application writes them to the outbox).</summary>
    public List<ClaimDomainEvent> NewEvents { get; } = new List<ClaimDomainEvent>();

    /// <summary>UC-2: a customer submits a claim (optionally with document names, UC-3).</summary>
    public static Claim Submit(NewClaimData data, DateTimeOffset now)
    {
        var claim = new Claim
        {
            Id = data.Id,
            CustomerId = data.CustomerId,
            Claimant = data.Claimant,
            PolicyNumber = data.PolicyNumber,
            Type = data.Type,
            IncidentDate = data.IncidentDate,
            Amount = data.Amount,
            Currency = data.Currency,
            Description = data.Description,
            Status = ClaimStatus.Submitted,
            AnalysisRound = 1,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

        claim.Log(now, HistoryEvents.Submitted, data.Claimant, "Claim submitted for " + TextUtil.Euro(data.Amount) + ".", true);

        foreach (var name in data.Documents)
        {
            claim.AttachDocument(name, null, data.CustomerId, now);
        }

        if (data.Documents.Count > 0)
        {
            claim.Log(now, HistoryEvents.DocumentAdded, data.Claimant, "Added: " + string.Join(", ", data.Documents) + ".", true);
        }

        claim.NewEvents.Add(new ClaimSubmittedEvent(Reanalysis: false));
        return claim;
    }

    /// <summary>
    /// UC-5 / UC-6: the agents' proposal arrived (claim.recommendation.created.v1).
    /// Approves the claim when the rules allow it, otherwise sends it to an employee (review.requested.v1).
    /// </summary>
    public ApplyResult ApplyRecommendation(RecommendationInput input, AutoApprovalOptions rules, DateTimeOffset now)
    {
        if (Status != ClaimStatus.Submitted)
        {
            return ApplyResult.Ignored("Claim " + Id + " is " + Status + ", not waiting for the agents.");
        }

        if (input.AnalysisRound.HasValue && input.AnalysisRound.Value != AnalysisRound)
        {
            return ApplyResult.Ignored(
                "Recommendation is for analysis round " + input.AnalysisRound.Value + ", the claim is in round " +
                AnalysisRound + ".");
        }

        RiskScore = input.RiskScore;
        RiskReasons = input.RiskReasons.ToList();
        Agents = input.Agents.ToList();
        RecommendationAction = input.Action;
        RecommendationSummary = input.Summary;
        AnalyzedAt = now;

        var dataStep = input.Agents.FirstOrDefault(a => !a.Agent.Contains("recommend", StringComparison.OrdinalIgnoreCase));
        if (dataStep is not null && !string.IsNullOrWhiteSpace(dataStep.Note))
        {
            Log(now, HistoryEvents.DataCollected, "AgentService", dataStep.Note, false);
        }

        if (input.RiskScore.HasValue)
        {
            Log(now, HistoryEvents.RiskAssessed, "RiskService", "Risk score " + TextUtil.Score(input.RiskScore.Value) + ".", false);
        }

        var summary = string.IsNullOrWhiteSpace(input.Summary)
            ? "Proposal: " + input.Action.ToString().ToUpperInvariant() + "."
            : input.Summary;
        Log(now, HistoryEvents.RecommendationCreated, "AgentService", summary, false);

        var reasons = AutoApprovalRules.ManualReviewReasons(Amount, Documents.Count, input, rules);

        if (reasons.Count == 0)
        {
            RecommendationAutomatic = true;
            ChangeStatus(ClaimStatus.Approved, "Approved automatically.", now);
            ClosedAt = now;
            Log(now, HistoryEvents.StatusChanged, ServiceActor, "Approved automatically.", true);
            Touch(now);
            return ApplyResult.Done("Approved automatically.");
        }

        RecommendationAutomatic = false;
        ChangeStatus(ClaimStatus.NeedsReview, "Sent to an employee for review.", now);
        Log(now, HistoryEvents.ReviewRequested, ServiceActor, "Sent to an employee for review.", true);
        Log(now, HistoryEvents.ReviewReasons, ServiceActor, "Manual review needed: " + string.Join(" ", reasons), false);
        NewEvents.Add(new ReviewRequestedEvent(reasons));
        Touch(now);
        return ApplyResult.Done("Sent to manual review: " + string.Join(" ", reasons));
    }

    /// <summary>UC-6: the employee decided (review.completed.v1).</summary>
    public ApplyResult ApplyReview(ReviewResultInput input, DateTimeOffset now)
    {
        if (Status != ClaimStatus.NeedsReview)
        {
            return ApplyResult.Ignored("Claim " + Id + " is " + Status + ", not waiting for a review.");
        }

        var comment = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();

        ReviewId = input.ReviewId;
        ReviewDecision = input.Decision;
        ReviewComment = comment;
        ReviewerId = input.ReviewerId;
        ReviewerName = input.ReviewerName;
        ReviewedAt = input.DecidedAt ?? now;

        var actor = input.ReviewerName ?? input.ReviewerId ?? "ReviewService";
        var commentText = comment is null ? string.Empty : " " + comment;

        if (input.Decision == Domain.ReviewDecision.NeedsInfo)
        {
            ChangeStatus(ClaimStatus.NeedsInfo, comment ?? "More documents needed.", now);
            Log(now, HistoryEvents.ReviewCompleted, actor, "Asked the customer for more documents." + commentText, true);
            Touch(now);
            return ApplyResult.Done("Waiting for more documents.");
        }

        var approved = input.Decision == Domain.ReviewDecision.Approved;
        var word = approved ? "Approved" : "Rejected";

        ChangeStatus(approved ? ClaimStatus.Approved : ClaimStatus.Rejected, comment ?? word + " by an employee.", now);
        ClosedAt = now;
        Log(now, HistoryEvents.ReviewCompleted, actor, word + "." + commentText, true);
        Log(now, HistoryEvents.StatusChanged, ServiceActor, "Final status: " + word.ToLowerInvariant() + ".", true);
        Touch(now);
        return ApplyResult.Done(word + " by an employee.");
    }

    /// <summary>
    /// UC-3 / UC-7: documents were added (document.added.v1). On a claim that waits for documents
    /// this starts a new analysis (claim.submitted.v1 again, with the next analysis round).
    /// </summary>
    public ApplyResult AddDocuments(DocumentAddedInput input, DateTimeOffset now)
    {
        var uploadedAt = input.UploadedAt ?? now;
        var added = new List<string>();
        var linked = 0;

        foreach (var document in input.Documents)
        {
            if (document.DocumentId is not null)
            {
                if (Documents.Any(d => d.DocumentId == document.DocumentId))
                {
                    continue; // already known
                }

                // The customer listed the file when submitting the claim; now DocumentService confirms it.
                var listed = Documents.FirstOrDefault(d =>
                    d.DocumentId is null && string.Equals(d.Name, document.Name, StringComparison.OrdinalIgnoreCase));
                if (listed is not null)
                {
                    listed.DocumentId = document.DocumentId;
                    linked++;
                    continue;
                }
            }
            else if (Documents.Any(d => string.Equals(d.Name, document.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // same name, no id to tell them apart
            }

            AttachDocument(document.Name, document.DocumentId, input.UploadedById, uploadedAt);
            added.Add(document.Name);
        }

        if (added.Count == 0)
        {
            if (linked > 0)
            {
                Touch(now);
                return ApplyResult.Done("Linked " + linked + " listed document(s) to DocumentService.");
            }

            return ApplyResult.Ignored("No new documents for claim " + Id + ".");
        }

        var actor = input.UploadedById is null || input.UploadedById == CustomerId
            ? Claimant
            : input.UploadedById;
        Log(now, HistoryEvents.DocumentAdded, actor, "Added: " + string.Join(", ", added) + ".", true);

        if (Status == ClaimStatus.NeedsInfo)
        {
            AnalysisRound++;
            ChangeStatus(ClaimStatus.Submitted, "New documents received.", now);
            Log(now, HistoryEvents.ReanalysisStarted, ServiceActor, "New documents received. Checking the claim again.", true);
            NewEvents.Add(new ClaimSubmittedEvent(Reanalysis: true));
            Touch(now);
            return ApplyResult.Done("Added " + added.Count + " document(s); re-analysis started (round " + AnalysisRound + ").");
        }

        Touch(now);
        return ApplyResult.Done("Added " + added.Count + " document(s).");
    }

    private void AttachDocument(string name, string? documentId, string? uploadedById, DateTimeOffset uploadedAt)
    {
        var document = new ClaimDocument
        {
            Id = Guid.NewGuid(),
            ClaimId = Id,
            Position = Documents.Count == 0 ? 1 : Documents.Max(d => d.Position) + 1,
            Name = name,
            DocumentId = documentId,
            UploadedAt = uploadedAt,
            UploadedById = uploadedById
        };

        Documents.Add(document);
        NewDocuments.Add(document);
    }

    private void ChangeStatus(ClaimStatus next, string? reason, DateTimeOffset now)
    {
        var previous = Status;
        Status = next;
        NewEvents.Add(new ClaimStatusChangedEvent(previous, next, reason));
        UpdatedAt = now;
    }

    private void Log(DateTimeOffset at, string eventName, string actor, string text, bool customerVisible)
    {
        HistoryCount++;
        NewHistory.Add(new ClaimHistoryEntry
        {
            ClaimId = Id,
            Seq = HistoryCount,
            At = at,
            Event = eventName,
            Actor = TextUtil.Truncate(actor, 200),
            Text = TextUtil.Truncate(text, 4000),
            CustomerVisible = customerVisible
        });
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}
