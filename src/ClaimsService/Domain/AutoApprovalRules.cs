using Horica.ClaimsService.Common;

namespace Horica.ClaimsService.Domain;

/// <summary>
/// Decides whether ClaimsService may approve a claim on its own, after the agents' proposal.
/// From the project plan: only with a valid policy, complete documents, an amount up to 300 € and a
/// risk score of at most 0.20. A proposed rejection, a missing score or a failed check always goes to an employee.
/// The agents never close a claim themselves; this is the only place where a claim is approved automatically.
/// </summary>
public static class AutoApprovalRules
{
    /// <summary>Why an employee has to look at the claim. Empty list = it may be approved automatically.</summary>
    public static IReadOnlyList<string> ManualReviewReasons(
        decimal amount,
        int documentCount,
        RecommendationInput recommendation,
        AutoApprovalOptions rules)
    {
        var reasons = new List<string>();

        if (!rules.Enabled)
        {
            reasons.Add("Automatic approval is turned off.");
        }

        switch (recommendation.Action)
        {
            case RecommendationAction.Reject:
                reasons.Add("The agents propose a rejection; an employee must confirm it.");
                break;
            case RecommendationAction.Review:
                reasons.Add("The agents asked for a manual review.");
                break;
        }

        if (amount > rules.MaxAmount)
        {
            reasons.Add("Amount " + TextUtil.Euro(amount) + " is above the auto-approval limit of " +
                        TextUtil.Euro(rules.MaxAmount) + ".");
        }

        if (recommendation.RiskScore is null)
        {
            reasons.Add("No risk score from RiskService.");
        }
        else if (recommendation.RiskScore.Value > rules.MaxRiskScore)
        {
            reasons.Add("Risk score " + TextUtil.Score(recommendation.RiskScore.Value) + " is above " +
                        TextUtil.Score(rules.MaxRiskScore) + ".");
        }

        if (rules.RequireDocuments && documentCount == 0)
        {
            reasons.Add("No supporting documents.");
        }

        if (recommendation.DocumentsComplete == false)
        {
            reasons.Add("Documents are not complete.");
        }

        if (recommendation.PolicyValid == false)
        {
            reasons.Add("The policy is not valid for this claim.");
        }

        if (recommendation.Agents.Any(a => a.Outcome == StepOutcome.Fail))
        {
            reasons.Add("An agent check failed.");
        }
        else if (recommendation.Agents.Any(a => a.Outcome == StepOutcome.Flag))
        {
            // FLAG = "a person should look" (an unclear case).
            reasons.Add("An agent flagged the claim for a person.");
        }

        return reasons;
    }
}
