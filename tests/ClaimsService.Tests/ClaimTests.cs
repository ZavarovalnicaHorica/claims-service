using Horica.ClaimsService.Domain;
using Xunit;

namespace Horica.ClaimsService.Tests;

public class ClaimTests
{
    [Fact]
    public void Submit_creates_a_submitted_claim_with_history_and_claim_submitted_event()
    {
        var claim = TestData.NewClaim(1200m);

        Assert.Equal("CLM-1001", claim.Id);
        Assert.Equal(ClaimStatus.Submitted, claim.Status);
        Assert.Equal(1, claim.AnalysisRound);
        Assert.Equal(2, claim.Documents.Count);
        Assert.Equal(new[] { 1, 2 }, claim.Documents.Select(d => d.Position));
        Assert.All(claim.Documents, d => Assert.Equal("cust-1", d.UploadedById));

        Assert.Equal(2, claim.NewHistory.Count);
        Assert.Equal(HistoryEvents.Submitted, claim.NewHistory[0].Event);
        Assert.Equal("Claim submitted for 1.200 €.", claim.NewHistory[0].Text);
        Assert.Equal(HistoryEvents.DocumentAdded, claim.NewHistory[1].Event);
        Assert.Equal("Added: damage-photo.jpg, invoice.pdf.", claim.NewHistory[1].Text);
        Assert.All(claim.NewHistory, h => Assert.True(h.CustomerVisible));
        Assert.Equal(new[] { 1, 2 }, claim.NewHistory.Select(h => h.Seq));

        var submitted = Assert.IsType<ClaimSubmittedEvent>(Assert.Single(claim.NewEvents));
        Assert.False(submitted.Reanalysis);
    }

    [Fact]
    public void Small_claim_with_documents_and_low_risk_is_approved_automatically()
    {
        var claim = TestData.NewClaim(250m);
        TestData.Clear(claim);

        var result = claim.ApplyRecommendation(TestData.Recommendation(riskScore: 0.12), TestData.Rules, TestData.Now);

        Assert.True(result.Applied);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
        Assert.True(claim.RecommendationAutomatic);
        Assert.Equal(0.12, claim.RiskScore);
        Assert.NotNull(claim.ClosedAt);

        var changed = Assert.IsType<ClaimStatusChangedEvent>(Assert.Single(claim.NewEvents));
        Assert.Equal(ClaimStatus.Submitted, changed.Previous);
        Assert.Equal(ClaimStatus.Approved, changed.Current);

        Assert.Contains(claim.NewHistory, h => h.Event == HistoryEvents.StatusChanged && h.CustomerVisible);
        Assert.Contains(claim.NewHistory, h => h.Event == HistoryEvents.RiskAssessed && !h.CustomerVisible);
        Assert.Contains(claim.NewHistory, h => h.Event == HistoryEvents.DataCollected && !h.CustomerVisible);
    }

    [Fact]
    public void Exactly_300_euro_and_risk_020_is_still_approved()
    {
        var claim = TestData.NewClaim(300m);

        claim.ApplyRecommendation(TestData.Recommendation(riskScore: 0.20), TestData.Rules, TestData.Now);

        Assert.Equal(ClaimStatus.Approved, claim.Status);
    }

    [Fact]
    public void Amount_above_the_limit_goes_to_an_employee()
    {
        var claim = TestData.NewClaim(1200m);
        TestData.Clear(claim);

        var result = claim.ApplyRecommendation(TestData.Recommendation(riskScore: 0.10), TestData.Rules, TestData.Now);

        Assert.True(result.Applied);
        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
        Assert.False(claim.RecommendationAutomatic);
        Assert.Null(claim.ClosedAt);

        var review = Assert.Single(claim.NewEvents.OfType<ReviewRequestedEvent>());
        Assert.Contains("Amount 1.200 € is above the auto-approval limit of 300 €.", review.Reasons);
        Assert.Single(claim.NewEvents.OfType<ClaimStatusChangedEvent>());

        Assert.Contains(claim.NewHistory, h => h.Event == HistoryEvents.ReviewRequested && h.CustomerVisible);
        Assert.Contains(claim.NewHistory, h => h.Event == HistoryEvents.ReviewReasons && !h.CustomerVisible);
    }

    [Fact]
    public void Risk_above_the_limit_goes_to_an_employee()
    {
        var claim = TestData.NewClaim(100m);

        claim.ApplyRecommendation(TestData.Recommendation(riskScore: 0.21), TestData.Rules, TestData.Now);

        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
        var review = Assert.Single(claim.NewEvents.OfType<ReviewRequestedEvent>());
        Assert.Contains("Risk score 0.21 is above 0.20.", review.Reasons);
    }

    [Fact]
    public void A_proposed_rejection_is_never_applied_automatically()
    {
        var claim = TestData.NewClaim(100m);

        claim.ApplyRecommendation(
            TestData.Recommendation(RecommendationAction.Reject, riskScore: 0.9, dataOutcome: StepOutcome.Fail, policyValid: false),
            TestData.Rules,
            TestData.Now);

        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
        Assert.Equal(RecommendationAction.Reject, claim.RecommendationAction);
    }

    [Fact]
    public void Missing_documents_go_to_an_employee()
    {
        var claim = TestData.NewClaimWithoutDocuments(100m);

        claim.ApplyRecommendation(TestData.Recommendation(riskScore: 0.05), TestData.Rules, TestData.Now);

        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
        var review = Assert.Single(claim.NewEvents.OfType<ReviewRequestedEvent>());
        Assert.Contains("No supporting documents.", review.Reasons);
    }

    [Fact]
    public void Missing_risk_score_goes_to_an_employee()
    {
        var claim = TestData.NewClaim(100m);

        claim.ApplyRecommendation(TestData.Recommendation(riskScore: null), TestData.Rules, TestData.Now);

        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
    }

    [Fact]
    public void A_flag_from_an_agent_goes_to_an_employee()
    {
        var claim = TestData.NewClaim(100m);

        claim.ApplyRecommendation(TestData.Recommendation(dataOutcome: StepOutcome.Flag), TestData.Rules, TestData.Now);

        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
    }

    [Fact]
    public void Incomplete_documents_or_invalid_policy_go_to_an_employee()
    {
        var first = TestData.NewClaim(100m);
        first.ApplyRecommendation(TestData.Recommendation(documentsComplete: false), TestData.Rules, TestData.Now);
        Assert.Equal(ClaimStatus.NeedsReview, first.Status);

        var second = TestData.NewClaim(100m);
        second.ApplyRecommendation(TestData.Recommendation(policyValid: false), TestData.Rules, TestData.Now);
        Assert.Equal(ClaimStatus.NeedsReview, second.Status);
    }

    [Fact]
    public void Disabled_auto_approval_sends_everything_to_an_employee()
    {
        var claim = TestData.NewClaim(50m);

        claim.ApplyRecommendation(TestData.Recommendation(riskScore: 0.01), new AutoApprovalOptions { Enabled = false }, TestData.Now);

        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
    }

    [Fact]
    public void A_second_recommendation_is_ignored()
    {
        var claim = TestData.ClaimInReview();
        var versionBefore = claim.Version;

        var result = claim.ApplyRecommendation(TestData.Recommendation(), TestData.Rules, TestData.Now);

        Assert.False(result.Applied);
        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
        Assert.Empty(claim.NewEvents);
        Assert.Empty(claim.NewHistory);
        Assert.Equal(versionBefore, claim.Version);
    }

    [Fact]
    public void A_recommendation_for_another_round_is_ignored()
    {
        var claim = TestData.NewClaim(100m);

        var result = claim.ApplyRecommendation(TestData.Recommendation(round: 2), TestData.Rules, TestData.Now);

        Assert.False(result.Applied);
        Assert.Equal(ClaimStatus.Submitted, claim.Status);
        Assert.Null(claim.RiskScore);
    }

    [Fact]
    public void An_approved_review_closes_the_claim()
    {
        var claim = TestData.ClaimInReview();

        var result = claim.ApplyReview(TestData.Review(ReviewDecision.Approved, "Invoice matches."), TestData.Now.AddHours(3));

        Assert.True(result.Applied);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
        Assert.Equal(ReviewDecision.Approved, claim.ReviewDecision);
        Assert.Equal("Invoice matches.", claim.ReviewComment);
        Assert.Equal("marko", claim.ReviewerName);
        Assert.Equal(TestData.Now.AddHours(2), claim.ReviewedAt);
        Assert.NotNull(claim.ClosedAt);

        var changed = Assert.IsType<ClaimStatusChangedEvent>(Assert.Single(claim.NewEvents));
        Assert.Equal(ClaimStatus.NeedsReview, changed.Previous);
        Assert.Equal(ClaimStatus.Approved, changed.Current);

        Assert.Equal(HistoryEvents.ReviewCompleted, claim.NewHistory[0].Event);
        Assert.Equal("marko", claim.NewHistory[0].Actor);
        Assert.Equal("Approved. Invoice matches.", claim.NewHistory[0].Text);
        Assert.Equal("Final status: approved.", claim.NewHistory[1].Text);
    }

    [Fact]
    public void A_rejected_review_closes_the_claim()
    {
        var claim = TestData.ClaimInReview();

        claim.ApplyReview(TestData.Review(ReviewDecision.Rejected, "Damage not covered."), TestData.Now);

        Assert.Equal(ClaimStatus.Rejected, claim.Status);
        Assert.True(claim.Status.IsFinal());
    }

    [Fact]
    public void Needs_more_info_waits_for_documents_and_new_documents_start_a_reanalysis()
    {
        var claim = TestData.ClaimInReview();

        claim.ApplyReview(TestData.Review(ReviewDecision.NeedsInfo, "Please add the plumber's report."), TestData.Now);
        Assert.Equal(ClaimStatus.NeedsInfo, claim.Status);
        Assert.Null(claim.ClosedAt);
        Assert.Equal("Asked the customer for more documents. Please add the plumber's report.", claim.NewHistory[0].Text);
        TestData.Clear(claim);

        var result = claim.AddDocuments(
            new DocumentAddedInput("CLM-1001", new[] { new IncomingDocument("plumber-report.pdf", "doc-9") }, "cust-1", null),
            TestData.Now.AddDays(1));

        Assert.True(result.Applied);
        Assert.Equal(ClaimStatus.Submitted, claim.Status);
        Assert.Equal(2, claim.AnalysisRound);
        Assert.Equal(3, claim.Documents.Count);
        Assert.Equal(3, claim.Documents.Last().Position);
        Assert.Equal("doc-9", claim.Documents.Last().DocumentId);

        Assert.Contains(claim.NewHistory, h => h.Event == HistoryEvents.DocumentAdded && h.Actor == "ana");
        Assert.Contains(claim.NewHistory, h => h.Event == HistoryEvents.ReanalysisStarted && h.CustomerVisible);

        var submitted = Assert.Single(claim.NewEvents.OfType<ClaimSubmittedEvent>());
        Assert.True(submitted.Reanalysis);
        var changed = Assert.Single(claim.NewEvents.OfType<ClaimStatusChangedEvent>());
        Assert.Equal(ClaimStatus.NeedsInfo, changed.Previous);
        Assert.Equal(ClaimStatus.Submitted, changed.Current);

        // The agents answer for round 2; an old answer for round 1 would be ignored.
        Assert.False(claim.ApplyRecommendation(TestData.Recommendation(round: 1), TestData.Rules, TestData.Now).Applied);
        Assert.True(claim.ApplyRecommendation(TestData.Recommendation(round: 2, riskScore: 0.3), TestData.Rules, TestData.Now).Applied);
        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
    }

    [Fact]
    public void A_review_is_ignored_when_the_claim_is_not_waiting_for_one()
    {
        var claim = TestData.NewClaim();

        var result = claim.ApplyReview(TestData.Review(ReviewDecision.Approved), TestData.Now);

        Assert.False(result.Applied);
        Assert.Equal(ClaimStatus.Submitted, claim.Status);
        Assert.Null(claim.ReviewDecision);
    }

    [Fact]
    public void A_final_claim_cannot_be_reopened_by_a_late_review()
    {
        var claim = TestData.ClaimInReview();
        claim.ApplyReview(TestData.Review(ReviewDecision.Rejected, "No."), TestData.Now);
        TestData.Clear(claim);

        var result = claim.ApplyReview(TestData.Review(ReviewDecision.Approved), TestData.Now);

        Assert.False(result.Applied);
        Assert.Equal(ClaimStatus.Rejected, claim.Status);
    }

    [Fact]
    public void The_same_document_name_without_an_id_is_not_added_twice()
    {
        var claim = TestData.NewClaim();
        TestData.Clear(claim);

        var result = claim.AddDocuments(
            new DocumentAddedInput("CLM-1001", new[] { new IncomingDocument("INVOICE.pdf", null) }, null, null),
            TestData.Now);

        Assert.False(result.Applied);
        Assert.Equal(2, claim.Documents.Count);
        Assert.Empty(claim.NewHistory);
    }

    [Fact]
    public void DocumentService_confirming_a_listed_file_links_it_instead_of_adding_it()
    {
        var claim = TestData.NewClaim();
        TestData.Clear(claim);

        var result = claim.AddDocuments(
            new DocumentAddedInput("CLM-1001", new[] { new IncomingDocument("invoice.pdf", "doc-1") }, "cust-1", null),
            TestData.Now);

        Assert.True(result.Applied);
        Assert.Equal(2, claim.Documents.Count);
        Assert.Equal("doc-1", claim.Documents.Single(d => d.Name == "invoice.pdf").DocumentId);
        Assert.Empty(claim.NewHistory);
        Assert.Empty(claim.NewEvents);

        // The same event again changes nothing.
        Assert.False(claim.AddDocuments(
            new DocumentAddedInput("CLM-1001", new[] { new IncomingDocument("invoice.pdf", "doc-1") }, "cust-1", null),
            TestData.Now).Applied);
    }

    [Fact]
    public void Documents_on_a_claim_in_review_are_recorded_without_a_reanalysis()
    {
        var claim = TestData.ClaimInReview();

        var result = claim.AddDocuments(
            new DocumentAddedInput("CLM-1001", new[] { new IncomingDocument("extra-photo.jpg", null) }, "staff-1", null),
            TestData.Now);

        Assert.True(result.Applied);
        Assert.Equal(ClaimStatus.NeedsReview, claim.Status);
        Assert.Equal(1, claim.AnalysisRound);
        Assert.Empty(claim.NewEvents);
        Assert.Equal("staff-1", Assert.Single(claim.NewHistory).Actor);
    }

    [Fact]
    public void History_sequence_continues_across_changes()
    {
        var claim = TestData.NewClaim(1200m);
        var afterSubmit = claim.HistoryCount;

        claim.ApplyRecommendation(TestData.Recommendation(), TestData.Rules, TestData.Now);

        Assert.Equal(afterSubmit + 1, claim.NewHistory.First(h => h.Seq > afterSubmit).Seq);
        Assert.Equal(claim.HistoryCount, claim.NewHistory.Max(h => h.Seq));
        Assert.Equal(claim.NewHistory.Count, claim.NewHistory.Select(h => h.Seq).Distinct().Count());
    }
}
