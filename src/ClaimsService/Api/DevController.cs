using System.ComponentModel.DataAnnotations;
using Horica.ClaimsService.Application;
using Horica.ClaimsService.Auth;
using Horica.ClaimsService.Common;
using Horica.ClaimsService.Domain;
using Horica.ClaimsService.Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Horica.ClaimsService.Api;

/// <summary>Body of POST /dev/claims/{id}/recommendation (as if AgentService sent claim.recommendation.created.v1).</summary>
public sealed class DevRecommendationBody
{
    /// <summary>APPROVE, REJECT or REVIEW.</summary>
    public string Recommendation { get; set; } = "APPROVE";

    [MaxLength(8000)]
    public string? Summary { get; set; } = "Policy is valid and the documents match the claim.";

    /// <summary>0 = no risk, 1 = highest risk.</summary>
    [Range(0.0, 1.0)]
    public double? RiskScore { get; set; } = 0.10;

    public List<string>? RiskReasons { get; set; }

    public bool? PolicyValid { get; set; } = true;

    public bool? DocumentsComplete { get; set; } = true;
}

/// <summary>Body of POST /dev/claims/{id}/review-completed (as if ReviewService sent review.completed.v1).</summary>
public sealed class DevReviewBody
{
    /// <summary>Approved, Rejected or NeedsMoreInfo (APPROVED, REJECTED, NEEDS_INFO also work).</summary>
    [Required]
    public string Decision { get; set; } = "Approved";

    [MaxLength(2000)]
    public string? Reason { get; set; } = "Documents are complete and consistent.";

    [MaxLength(200)]
    public string? ReviewerName { get; set; } = "dev-reviewer";
}

/// <summary>Body of POST /dev/claims/{id}/documents (as if DocumentService sent document.added.v1).</summary>
public sealed class DevDocumentsBody
{
    [Required]
    [MinLength(1)]
    public List<string> Names { get; set; } = new List<string> { "invoice.pdf" };
}

public sealed record DevEventResult(bool Applied, string Note, ClaimDto Claim);

/// <summary>
/// Development helpers: feed the events of the other services without Kafka (and without AgentService,
/// ReviewService or DocumentService running). They use exactly the same code as the Kafka consumer.
/// Return 404 outside the Development environment.
/// </summary>
[ApiController]
[Route("dev/claims/{id}")]
[Produces("application/json")]
[Authorize(Policy = AuthPolicies.Staff)]
public sealed class DevController : ControllerBase
{
    private readonly IncomingEventHandler _handler;
    private readonly ClaimService _claims;
    private readonly IWebHostEnvironment _environment;
    private readonly AuthOptions _authOptions;

    public DevController(
        IncomingEventHandler handler,
        ClaimService claims,
        IWebHostEnvironment environment,
        IOptions<AuthOptions> authOptions)
    {
        _handler = handler;
        _claims = claims;
        _environment = environment;
        _authOptions = authOptions.Value;
    }

    /// <summary>The agents' proposal arrived: the claim is approved automatically or sent to an employee.</summary>
    [HttpPost("recommendation")]
    public async Task<ActionResult<DevEventResult>> Recommendation(
        string id,
        [FromBody] DevRecommendationBody body,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var score = body.RiskScore;
        var input = new RecommendationInput(
            ClaimId: id,
            Action: IncomingEventParsers.ParseAction(body.Recommendation),
            Summary: body.Summary,
            RiskScore: score,
            RiskReasons: body.RiskReasons ?? new List<string>(),
            Agents: new List<AgentStep>
            {
                new AgentStep(
                    "Data agent",
                    body.PolicyValid == false ? StepOutcome.Fail : StepOutcome.Pass,
                    body.PolicyValid == false ? "Policy is not valid for this claim." : "Policy is valid. Documents collected."),
                new AgentStep(
                    "Recommendation agent",
                    IncomingEventParsers.ParseAction(body.Recommendation) == RecommendationAction.Approve ? StepOutcome.Pass : StepOutcome.Flag,
                    body.Summary ?? string.Empty)
            },
            PolicyValid: body.PolicyValid,
            DocumentsComplete: body.DocumentsComplete,
            AnalysisRound: null);

        var result = await _handler.ApplyRecommendationAsync(
            new Parsed<RecommendationInput>(DevEventId(), input),
            cancellationToken);

        return await ResultAsync(id, result, cancellationToken);
    }

    /// <summary>The employee decided: the claim is approved, rejected or waits for documents.</summary>
    [HttpPost("review-completed")]
    public async Task<ActionResult<DevEventResult>> ReviewCompleted(
        string id,
        [FromBody] DevReviewBody body,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        if (!IncomingEventParsers.TryParseDecision(body.Decision, out var decision))
        {
            throw new InvalidRequestException("'decision' must be Approved, Rejected or NeedsMoreInfo.");
        }

        var user = User.ToCurrentUser(_authOptions);
        var input = new ReviewResultInput(
            ClaimId: id,
            Decision: decision,
            Reason: body.Reason,
            ReviewId: "dev-" + Guid.NewGuid().ToString("N"),
            ReviewerId: user.Id,
            ReviewerName: body.ReviewerName ?? user.DisplayName,
            DecidedAt: null);

        var result = await _handler.ApplyReviewAsync(
            new Parsed<ReviewResultInput>(DevEventId(), input),
            cancellationToken);

        return await ResultAsync(id, result, cancellationToken);
    }

    /// <summary>Documents were added. A claim in NEEDS_INFO is checked again (UC-7).</summary>
    [HttpPost("documents")]
    public async Task<ActionResult<DevEventResult>> Documents(
        string id,
        [FromBody] DevDocumentsBody body,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var names = body.Names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => TextUtil.Truncate(n.Trim(), ClaimService.MaxDocumentNameLength))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
        {
            throw new InvalidRequestException("'names' must contain at least one file name.");
        }

        var input = new DocumentAddedInput(
            ClaimId: id,
            Documents: names.Select(n => new IncomingDocument(n, null)).ToList(),
            UploadedById: null,
            UploadedAt: null);

        var result = await _handler.ApplyDocumentsAsync(
            new Parsed<DocumentAddedInput>(DevEventId(), input),
            cancellationToken);

        return await ResultAsync(id, result, cancellationToken);
    }

    private async Task<ActionResult<DevEventResult>> ResultAsync(string id, ApplyResult result, CancellationToken cancellationToken)
    {
        var claim = await _claims.GetAsync(id, User.ToCurrentUser(_authOptions), cancellationToken);
        return Ok(new DevEventResult(result.Applied, result.Note, claim));
    }

    private static string DevEventId()
    {
        return "dev:" + Guid.NewGuid().ToString("N");
    }
}
