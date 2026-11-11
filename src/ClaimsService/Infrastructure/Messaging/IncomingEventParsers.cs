using System.Text.Json;
using Horica.ClaimsService.Common;
using Horica.ClaimsService.Domain;

namespace Horica.ClaimsService.Infrastructure.Messaging;

public sealed record Parsed<T>(string EventId, T Data);

/// <summary>
/// Reads the events ClaimsService consumes. Each accepts a CloudEvents envelope or just the data object.
/// Only claimId (and for review.completed.v1 the decision) is mandatory; everything else is optional.
/// </summary>
public static class IncomingEventParsers
{
    private const int MaxIdLength = 100;
    private const int MaxNameLength = 255;
    private const int MaxReasonLength = 500;
    private const int MaxReasonCount = 50;
    private const int MaxSummaryLength = 8000;
    private const int MaxNoteLength = 2000;
    private const int MaxAgentSteps = 20;
    private const int MaxDocuments = 50;

    /// <summary>
    /// claim.recommendation.created.v1 (AgentService). Example data:
    /// { "claimId": "CLM-1001", "recommendation": "APPROVE", "summary": "...", "riskScore": 0.12,
    ///   "riskReasons": ["..."], "policyValid": true, "documentsComplete": true, "analysisRound": 1,
    ///   "agents": [{ "agent": "Data agent", "outcome": "PASS", "note": "..." }] }
    /// Also accepted: "action" instead of "recommendation", nested "recommendation": { "action", "summary" }
    /// and nested "risk": { "score", "reasons" }.
    /// </summary>
    public static Parsed<RecommendationInput> ParseRecommendation(string? json, string fallbackEventId)
    {
        using var document = JsonRead.ParseDocument(json);
        var (eventId, data) = JsonRead.Unwrap(document.RootElement, fallbackEventId);
        var claimId = ReadClaimId(data);

        string? actionText = null;
        string? summary = null;

        if (JsonRead.TryGetProperty(data, "recommendation", out var recommendation) &&
            recommendation.ValueKind == JsonValueKind.Object)
        {
            actionText = JsonRead.ReadString(recommendation, "action", "decision", "recommendation");
            summary = JsonRead.ReadString(recommendation, MaxSummaryLength, "summary", "text");
        }

        // A nested "recommendation" object is not a scalar, so it is skipped here.
        actionText ??= JsonRead.ReadString(data, "recommendation", "action", "proposal", "decision");
        summary ??= JsonRead.ReadString(data, MaxSummaryLength, "summary", "agentSummary");

        double? score;
        List<string> reasons;

        if (JsonRead.TryGetProperty(data, "risk", out var risk) && risk.ValueKind == JsonValueKind.Object)
        {
            score = JsonRead.ReadDouble(risk, "score", "riskScore");
            reasons = JsonRead.ReadStringList(risk, MaxReasonCount, MaxReasonLength, "reasons", "riskReasons");
        }
        else
        {
            score = JsonRead.ReadDouble(data, "riskScore", "risk_score", "score");
            reasons = JsonRead.ReadStringList(data, MaxReasonCount, MaxReasonLength, "riskReasons", "reasons");
        }

        if (score.HasValue)
        {
            if (double.IsNaN(score.Value) || double.IsInfinity(score.Value))
            {
                score = null;
            }
            else
            {
                // A score outside 0..1 is clamped; a high value always ends up with an employee anyway.
                score = Math.Round(Math.Clamp(score.Value, 0.0, 1.0), 4);
            }
        }

        var input = new RecommendationInput(
            ClaimId: claimId,
            Action: ParseAction(actionText),
            Summary: summary,
            RiskScore: score,
            RiskReasons: reasons,
            Agents: ReadAgents(data),
            PolicyValid: JsonRead.ReadBool(data, "policyValid", "policy_valid"),
            DocumentsComplete: JsonRead.ReadBool(data, "documentsComplete", "documents_complete"),
            AnalysisRound: JsonRead.ReadInt(data, "analysisRound", "analysis_round", "round"));

        return new Parsed<RecommendationInput>(eventId, input);
    }

    /// <summary>
    /// review.completed.v1 (ReviewService). Example data:
    /// { "reviewId": "...", "claimId": "CLM-2001", "decision": "Approved", "reason": "...",
    ///   "reviewerId": "...", "reviewerName": "ana", "decidedAt": "..." }
    /// Decision: Approved | Rejected | NeedsMoreInfo (also APPROVED, REJECTED, NEEDS_INFO).
    /// </summary>
    public static Parsed<ReviewResultInput> ParseReviewCompleted(string? json, string fallbackEventId)
    {
        using var document = JsonRead.ParseDocument(json);
        var (eventId, data) = JsonRead.Unwrap(document.RootElement, fallbackEventId);
        var claimId = ReadClaimId(data);

        var decisionText = JsonRead.ReadString(data, "decision", "outcome", "result");
        if (decisionText is null)
        {
            throw new InvalidMessageException("Field 'decision' is required.");
        }

        if (!TryParseDecision(decisionText, out var decision))
        {
            throw new InvalidMessageException("Unknown decision '" + TextUtil.Truncate(decisionText, 50) + "'.");
        }

        var input = new ReviewResultInput(
            ClaimId: claimId,
            Decision: decision,
            Reason: JsonRead.ReadString(data, 2000, "reason", "comment"),
            ReviewId: JsonRead.ReadString(data, MaxIdLength, "reviewId", "review_id"),
            ReviewerId: JsonRead.ReadString(data, 200, "reviewerId", "reviewer_id", "decidedBy"),
            ReviewerName: JsonRead.ReadString(data, 200, "reviewerName", "reviewer_name", "decidedByName"),
            DecidedAt: JsonRead.ReadTime(data, "decidedAt", "decided_at"));

        return new Parsed<ReviewResultInput>(eventId, input);
    }

    /// <summary>
    /// document.added.v1 (DocumentService). Example data:
    /// { "claimId": "CLM-1001", "documentId": "...", "name": "invoice.pdf", "uploadedById": "...", "uploadedAt": "..." }
    /// Also accepted: "fileName" instead of "name", or "documents": [{ "documentId", "name" }] / "names": ["a.jpg"].
    /// </summary>
    public static Parsed<DocumentAddedInput> ParseDocumentAdded(string? json, string fallbackEventId)
    {
        using var document = JsonRead.ParseDocument(json);
        var (eventId, data) = JsonRead.Unwrap(document.RootElement, fallbackEventId);
        var claimId = ReadClaimId(data);

        var documents = new List<IncomingDocument>();

        var singleName = JsonRead.ReadString(data, MaxNameLength, "name", "fileName", "file_name", "documentName", "filename");
        if (singleName is not null)
        {
            documents.Add(new IncomingDocument(singleName, JsonRead.ReadString(data, 200, "documentId", "document_id", "id")));
        }

        if (JsonRead.TryGetProperty(data, "documents", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (documents.Count >= MaxDocuments)
                {
                    break;
                }

                if (item.ValueKind == JsonValueKind.Object)
                {
                    var name = JsonRead.ReadString(item, MaxNameLength, "name", "fileName", "file_name", "documentName", "filename");
                    if (name is not null)
                    {
                        documents.Add(new IncomingDocument(name, JsonRead.ReadString(item, 200, "documentId", "document_id", "id")));
                    }
                }
                else
                {
                    var name = JsonRead.ScalarToString(item);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        documents.Add(new IncomingDocument(TextUtil.Truncate(name.Trim(), MaxNameLength), null));
                    }
                }
            }
        }

        foreach (var name in JsonRead.ReadStringList(data, MaxDocuments, MaxNameLength, "names", "fileNames"))
        {
            documents.Add(new IncomingDocument(name, null));
        }

        if (documents.Count == 0)
        {
            throw new InvalidMessageException("The event names no document ('name', 'documents' or 'names').");
        }

        var input = new DocumentAddedInput(
            ClaimId: claimId,
            Documents: documents
                .GroupBy(d => (d.Name.ToUpperInvariant(), d.DocumentId))
                .Select(g => g.First())
                .Take(MaxDocuments)
                .ToList(),
            UploadedById: JsonRead.ReadString(data, 200, "uploadedById", "uploadedBy", "uploaded_by", "userId", "customerId"),
            UploadedAt: JsonRead.ReadTime(data, "uploadedAt", "uploaded_at", "addedAt", "createdAt"));

        return new Parsed<DocumentAddedInput>(eventId, input);
    }

    public static RecommendationAction ParseAction(string? value)
    {
        var normalized = Normalize(value);
        switch (normalized)
        {
            case "APPROVE":
            case "APPROVED":
            case "ACCEPT":
                return RecommendationAction.Approve;
            case "REJECT":
            case "REJECTED":
            case "DENY":
                return RecommendationAction.Reject;
            default:
                // REVIEW, MANUALREVIEW, NEEDSREVIEW, unknown or missing: a person decides.
                return RecommendationAction.Review;
        }
    }

    public static bool TryParseDecision(string value, out ReviewDecision decision)
    {
        switch (Normalize(value))
        {
            case "APPROVED":
            case "APPROVE":
                decision = ReviewDecision.Approved;
                return true;
            case "REJECTED":
            case "REJECT":
                decision = ReviewDecision.Rejected;
                return true;
            case "NEEDSMOREINFO":
            case "NEEDSINFO":
            case "MOREINFO":
            case "REQUESTINFO":
                decision = ReviewDecision.NeedsInfo;
                return true;
            default:
                decision = ReviewDecision.Approved;
                return false;
        }
    }

    public static bool TryParseOutcome(string? value, out StepOutcome outcome)
    {
        switch (Normalize(value))
        {
            case "PASS":
            case "OK":
                outcome = StepOutcome.Pass;
                return true;
            case "FLAG":
            case "WARN":
            case "WARNING":
                outcome = StepOutcome.Flag;
                return true;
            case "FAIL":
            case "FAILED":
                outcome = StepOutcome.Fail;
                return true;
            default:
                outcome = StepOutcome.Flag;
                return false;
        }
    }

    private static List<AgentStep> ReadAgents(JsonElement data)
    {
        var steps = new List<AgentStep>();

        if (!JsonRead.TryGetAny(data, out var agents, "agents", "steps", "agentSteps") ||
            agents.ValueKind != JsonValueKind.Array)
        {
            return steps;
        }

        foreach (var item in agents.EnumerateArray())
        {
            if (steps.Count >= MaxAgentSteps)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var agent = JsonRead.ReadString(item, 100, "agent", "name") ?? "Agent";
            // An unknown outcome is treated as FLAG: a person should look.
            TryParseOutcome(JsonRead.ReadString(item, "outcome", "result", "status"), out var outcome);
            var note = JsonRead.ReadString(item, MaxNoteLength, "note", "text", "message") ?? string.Empty;

            steps.Add(new AgentStep(agent, outcome, note));
        }

        return steps;
    }

    private static string ReadClaimId(JsonElement data)
    {
        var claimId = JsonRead.ReadString(data, "claimId", "claim_id");
        if (string.IsNullOrWhiteSpace(claimId))
        {
            throw new InvalidMessageException("Field 'claimId' is required.");
        }

        if (claimId.Length > MaxIdLength)
        {
            throw new InvalidMessageException("Field 'claimId' is longer than " + MaxIdLength + " characters.");
        }

        return claimId;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(char.IsLetter).ToArray()).ToUpperInvariant();
    }
}
