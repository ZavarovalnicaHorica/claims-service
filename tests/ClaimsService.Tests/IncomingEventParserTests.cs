using Horica.ClaimsService.Domain;
using Horica.ClaimsService.Infrastructure.Messaging;
using Xunit;

namespace Horica.ClaimsService.Tests;

public class IncomingEventParserTests
{
    // Exactly what review-service publishes (review-service/samples/review-completed.json).
    private const string ReviewServiceSample = """
    {
      "specversion": "1.0",
      "id": "5b0c8f7e-3a53-4b53-9d1e-7a6a2d4f9c10",
      "source": "/horica/review-service",
      "type": "review.completed.v1",
      "subject": "claims/CLM-2001",
      "time": "2026-11-07T10:12:31.5120000+00:00",
      "datacontenttype": "application/json",
      "data": {
        "reviewId": "0f6f1d4a-6a9e-4b0f-8f3e-1f0f2f2f9a11",
        "claimId": "CLM-2001",
        "decision": "Approved",
        "reason": "Documents are complete and consistent.",
        "reviewerId": "a3f1c2d4-0000-0000-0000-000000000001",
        "reviewerName": "ana",
        "decidedAt": "2026-11-07T10:12:31.4980000+00:00"
      }
    }
    """;

    [Fact]
    public void Reads_review_completed_from_review_service()
    {
        var parsed = IncomingEventParsers.ParseReviewCompleted(ReviewServiceSample, "fallback");

        Assert.Equal("5b0c8f7e-3a53-4b53-9d1e-7a6a2d4f9c10", parsed.EventId);
        Assert.Equal("CLM-2001", parsed.Data.ClaimId);
        Assert.Equal(ReviewDecision.Approved, parsed.Data.Decision);
        Assert.Equal("Documents are complete and consistent.", parsed.Data.Reason);
        Assert.Equal("0f6f1d4a-6a9e-4b0f-8f3e-1f0f2f2f9a11", parsed.Data.ReviewId);
        Assert.Equal("ana", parsed.Data.ReviewerName);
        Assert.Equal(TimeSpan.Zero, parsed.Data.DecidedAt!.Value.Offset);
    }

    [Theory]
    [InlineData("NeedsMoreInfo", ReviewDecision.NeedsInfo)]
    [InlineData("NEEDS_INFO", ReviewDecision.NeedsInfo)]
    [InlineData("needs-more-info", ReviewDecision.NeedsInfo)]
    [InlineData("REJECTED", ReviewDecision.Rejected)]
    [InlineData("rejected", ReviewDecision.Rejected)]
    [InlineData("APPROVED", ReviewDecision.Approved)]
    public void Reads_every_spelling_of_a_decision(string text, ReviewDecision expected)
    {
        var parsed = IncomingEventParsers.ParseReviewCompleted(
            "{ \"claimId\": \"CLM-1\", \"decision\": \"" + text + "\" }",
            "fallback");

        Assert.Equal(expected, parsed.Data.Decision);
        Assert.Equal("fallback", parsed.EventId);
    }

    [Fact]
    public void An_unknown_or_missing_decision_is_invalid()
    {
        Assert.Throws<InvalidMessageException>(() =>
            IncomingEventParsers.ParseReviewCompleted("""{ "claimId": "CLM-1", "decision": "maybe" }""", "x"));
        Assert.Throws<InvalidMessageException>(() =>
            IncomingEventParsers.ParseReviewCompleted("""{ "claimId": "CLM-1" }""", "x"));
    }

    [Fact]
    public void A_time_with_an_offset_is_stored_as_utc()
    {
        var parsed = IncomingEventParsers.ParseReviewCompleted(
            """{ "claimId": "CLM-1", "decision": "Approved", "decidedAt": "2026-11-07T12:00:00+02:00" }""",
            "x");

        Assert.Equal(TimeSpan.Zero, parsed.Data.DecidedAt!.Value.Offset);
        Assert.Equal(10, parsed.Data.DecidedAt.Value.Hour);
    }

    [Fact]
    public void Reads_a_flat_recommendation()
    {
        const string json = """
        {
          "specversion": "1.0", "id": "rec-1", "type": "claim.recommendation.created.v1",
          "data": {
            "claimId": "CLM-1001",
            "recommendation": "APPROVE",
            "summary": "All good.",
            "riskScore": 0.12,
            "riskReasons": ["Amount is 40 % of the policy limit"],
            "policyValid": true,
            "documentsComplete": true,
            "analysisRound": 1,
            "agents": [
              { "agent": "Data agent", "outcome": "PASS", "note": "Policy valid." },
              { "agent": "Recommendation agent", "outcome": "pass", "note": "All good." }
            ]
          }
        }
        """;

        var parsed = IncomingEventParsers.ParseRecommendation(json, "fallback");

        Assert.Equal("rec-1", parsed.EventId);
        Assert.Equal("CLM-1001", parsed.Data.ClaimId);
        Assert.Equal(RecommendationAction.Approve, parsed.Data.Action);
        Assert.Equal("All good.", parsed.Data.Summary);
        Assert.Equal(0.12, parsed.Data.RiskScore);
        Assert.Single(parsed.Data.RiskReasons);
        Assert.True(parsed.Data.PolicyValid);
        Assert.True(parsed.Data.DocumentsComplete);
        Assert.Equal(1, parsed.Data.AnalysisRound);
        Assert.Equal(2, parsed.Data.Agents.Count);
        Assert.All(parsed.Data.Agents, a => Assert.Equal(StepOutcome.Pass, a.Outcome));
    }

    [Fact]
    public void Reads_a_nested_recommendation_like_the_web_ui_model()
    {
        const string json = """
        {
          "claimId": "CLM-1001",
          "recommendation": { "action": "REJECT", "summary": "Policy expired.", "automatic": false },
          "risk": { "score": "0.65", "reasons": ["Policy does not cover the claim"] }
        }
        """;

        var parsed = IncomingEventParsers.ParseRecommendation(json, "fallback");

        Assert.Equal(RecommendationAction.Reject, parsed.Data.Action);
        Assert.Equal("Policy expired.", parsed.Data.Summary);
        Assert.Equal(0.65, parsed.Data.RiskScore);
        Assert.Equal("Policy does not cover the claim", Assert.Single(parsed.Data.RiskReasons));
        Assert.Null(parsed.Data.AnalysisRound);
    }

    [Fact]
    public void An_unknown_action_means_a_person_decides()
    {
        var parsed = IncomingEventParsers.ParseRecommendation("""{ "claimId": "CLM-1", "action": "whatever" }""", "x");

        Assert.Equal(RecommendationAction.Review, parsed.Data.Action);
        Assert.Null(parsed.Data.RiskScore);
    }

    [Fact]
    public void A_risk_score_outside_0_to_1_is_clamped()
    {
        var parsed = IncomingEventParsers.ParseRecommendation("""{ "claimId": "CLM-1", "riskScore": 45 }""", "x");

        Assert.Equal(1.0, parsed.Data.RiskScore);
    }

    [Fact]
    public void An_unknown_agent_outcome_counts_as_a_flag()
    {
        var parsed = IncomingEventParsers.ParseRecommendation(
            """{ "claimId": "CLM-1", "agents": [{ "agent": "Data agent", "outcome": "???", "note": "hm" }] }""",
            "x");

        Assert.Equal(StepOutcome.Flag, Assert.Single(parsed.Data.Agents).Outcome);
    }

    [Fact]
    public void Reads_a_single_added_document()
    {
        var parsed = IncomingEventParsers.ParseDocumentAdded(
            """{ "claimId": "CLM-7", "documentId": "d-1", "fileName": "photo.jpg", "uploadedBy": "cust-1", "uploadedAt": "2026-11-07T10:00:00Z" }""",
            "x");

        var document = Assert.Single(parsed.Data.Documents);
        Assert.Equal("photo.jpg", document.Name);
        Assert.Equal("d-1", document.DocumentId);
        Assert.Equal("cust-1", parsed.Data.UploadedById);
        Assert.NotNull(parsed.Data.UploadedAt);
    }

    [Fact]
    public void Reads_a_list_of_added_documents()
    {
        var parsed = IncomingEventParsers.ParseDocumentAdded(
            """{ "claimId": "CLM-7", "documents": [{ "documentId": "d-1", "name": "a.jpg" }, "b.pdf", { "name": "a.jpg", "documentId": "d-1" }] }""",
            "x");

        Assert.Equal(2, parsed.Data.Documents.Count);
        Assert.Equal("a.jpg", parsed.Data.Documents[0].Name);
        Assert.Equal("b.pdf", parsed.Data.Documents[1].Name);
        Assert.Null(parsed.Data.Documents[1].DocumentId);
    }

    [Fact]
    public void Reads_document_names_like_the_web_ui_sends_them()
    {
        var parsed = IncomingEventParsers.ParseDocumentAdded("""{ "claimId": "CLM-7", "names": ["a.jpg", "b.pdf"] }""", "x");

        Assert.Equal(new[] { "a.jpg", "b.pdf" }, parsed.Data.Documents.Select(d => d.Name));
    }

    [Fact]
    public void A_document_event_without_a_name_is_invalid()
    {
        Assert.Throws<InvalidMessageException>(() =>
            IncomingEventParsers.ParseDocumentAdded("""{ "claimId": "CLM-7" }""", "x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{ "decision": "Approved" }""")]
    [InlineData("""{ "specversion": "1.0", "id": "x" }""")]
    public void Broken_messages_are_invalid(string json)
    {
        Assert.Throws<InvalidMessageException>(() => IncomingEventParsers.ParseReviewCompleted(json, "x"));
        Assert.Throws<InvalidMessageException>(() => IncomingEventParsers.ParseRecommendation(json, "x"));
        Assert.Throws<InvalidMessageException>(() => IncomingEventParsers.ParseDocumentAdded(json, "x"));
    }

    [Fact]
    public void A_numeric_claim_id_is_accepted()
    {
        var parsed = IncomingEventParsers.ParseRecommendation("""{ "claimId": 1001 }""", "x");

        Assert.Equal("1001", parsed.Data.ClaimId);
    }
}
