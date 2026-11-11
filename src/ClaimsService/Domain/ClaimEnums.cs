namespace Horica.ClaimsService.Domain;

/// <summary>
/// State of a claim. ClaimsService owns it. In JSON: SUBMITTED, NEEDS_REVIEW, NEEDS_INFO, APPROVED, REJECTED.
/// </summary>
public enum ClaimStatus
{
    /// <summary>Received; waiting for the agents (claim.submitted.v1 was published).</summary>
    Submitted = 0,

    /// <summary>An employee has to decide (review.requested.v1 was published).</summary>
    NeedsReview = 1,

    /// <summary>The employee asked the customer for more documents (UC-7).</summary>
    NeedsInfo = 2,

    /// <summary>Final.</summary>
    Approved = 3,

    /// <summary>Final.</summary>
    Rejected = 4
}

/// <summary>Proposal of the agents. In JSON: APPROVE, REJECT, REVIEW.</summary>
public enum RecommendationAction
{
    Approve = 0,
    Reject = 1,
    Review = 2
}

/// <summary>Decision of an employee. In JSON: APPROVED, REJECTED, NEEDS_INFO.</summary>
public enum ReviewDecision
{
    Approved = 0,
    Rejected = 1,
    NeedsInfo = 2
}

/// <summary>Result of one agent check. In JSON: PASS, FLAG, FAIL.</summary>
public enum StepOutcome
{
    Pass = 0,
    Flag = 1,
    Fail = 2
}

public static class ClaimStatusExtensions
{
    public static bool IsFinal(this ClaimStatus status)
    {
        return status == ClaimStatus.Approved || status == ClaimStatus.Rejected;
    }

    /// <summary>Accepts "NEEDS_REVIEW", "NeedsReview", "needs-review" and similar.</summary>
    public static bool TryParse(string? value, out ClaimStatus status)
    {
        status = ClaimStatus.Submitted;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
        return Enum.TryParse(normalized, ignoreCase: true, out status) && Enum.IsDefined(status);
    }
}
