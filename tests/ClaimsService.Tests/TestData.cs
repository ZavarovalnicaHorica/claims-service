using Horica.ClaimsService.Domain;

namespace Horica.ClaimsService.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    public static readonly AutoApprovalOptions Rules = new AutoApprovalOptions();

    public static Claim NewClaim(decimal amount = 1200m, params string[] documents)
    {
        if (documents.Length == 0)
        {
            documents = new[] { "damage-photo.jpg", "invoice.pdf" };
        }

        return Claim.Submit(
            new NewClaimData(
                Id: "CLM-1001",
                CustomerId: "cust-1",
                Claimant: "ana",
                PolicyNumber: "POL-1001",
                Type: "CAR",
                IncidentDate: new DateOnly(2026, 11, 1),
                Amount: amount,
                Currency: "EUR",
                Description: "Another driver reversed into my parked car.",
                Documents: documents),
            Now);
    }

    public static Claim NewClaimWithoutDocuments(decimal amount = 150m)
    {
        return Claim.Submit(
            new NewClaimData(
                Id: "CLM-1002",
                CustomerId: "cust-1",
                Claimant: "ana",
                PolicyNumber: "POL-1001",
                Type: "CAR",
                IncidentDate: new DateOnly(2026, 11, 1),
                Amount: amount,
                Currency: "EUR",
                Description: "Stone chip in the windscreen.",
                Documents: Array.Empty<string>()),
            Now);
    }

    public static RecommendationInput Recommendation(
        RecommendationAction action = RecommendationAction.Approve,
        double? riskScore = 0.10,
        int? round = null,
        StepOutcome dataOutcome = StepOutcome.Pass,
        bool? policyValid = true,
        bool? documentsComplete = true)
    {
        return new RecommendationInput(
            ClaimId: "CLM-1001",
            Action: action,
            Summary: "Policy valid, documents match.",
            RiskScore: riskScore,
            RiskReasons: new[] { "Amount is 40 % of the policy limit" },
            Agents: new[]
            {
                new AgentStep("Data agent", dataOutcome, "Policy POL-1001 is valid. 2 document(s) collected."),
                new AgentStep("Recommendation agent", StepOutcome.Pass, "Policy valid, documents match.")
            },
            PolicyValid: policyValid,
            DocumentsComplete: documentsComplete,
            AnalysisRound: round);
    }

    public static ReviewResultInput Review(ReviewDecision decision, string? reason = "Looks fine.")
    {
        return new ReviewResultInput(
            ClaimId: "CLM-1001",
            Decision: decision,
            Reason: reason,
            ReviewId: "rev-1",
            ReviewerId: "user-marko",
            ReviewerName: "marko",
            DecidedAt: Now.AddHours(2));
    }

    /// <summary>A claim the agents sent to an employee (1.200 € is above the limit).</summary>
    public static Claim ClaimInReview()
    {
        var claim = NewClaim(1200m);
        claim.ApplyRecommendation(Recommendation(riskScore: 0.45), Rules, Now.AddMinutes(1));
        Clear(claim);
        return claim;
    }

    /// <summary>Forgets what the last change produced, as if it had been saved.</summary>
    public static void Clear(Claim claim)
    {
        claim.NewEvents.Clear();
        claim.NewHistory.Clear();
        claim.NewDocuments.Clear();
    }
}
