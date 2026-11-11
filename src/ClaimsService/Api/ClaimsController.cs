using Horica.ClaimsService.Application;
using Horica.ClaimsService.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Horica.ClaimsService.Api;

/// <summary>Claims, their status and their handling history.</summary>
[ApiController]
[Route("claims")]
[Produces("application/json")]
[Authorize(Policy = AuthPolicies.ClaimsUser)]
public sealed class ClaimsController : ControllerBase
{
    private readonly ClaimService _service;
    private readonly AuthOptions _authOptions;

    public ClaimsController(ClaimService service, IOptions<AuthOptions> authOptions)
    {
        _service = service;
        _authOptions = authOptions.Value;
    }

    /// <summary>UC-2: a customer submits a claim. Status SUBMITTED; claim.submitted.v1 is published.</summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.Customer)]
    [ProducesResponseType(typeof(ClaimDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ClaimDto>> Submit([FromBody] SubmitClaimRequest request, CancellationToken cancellationToken)
    {
        var claim = await _service.SubmitAsync(request, CurrentUser(), BearerToken(), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = claim.Id }, claim);
    }

    /// <summary>UC-4: customers get their own claims, employees every claim. Newest first.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ClaimDto>>> List([FromQuery] ClaimListQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _service.ListAsync(query, CurrentUser(), cancellationToken));
    }

    /// <summary>UC-8: every history entry of every claim, newest first (managers).</summary>
    [HttpGet("history")]
    [Authorize(Policy = AuthPolicies.Manager)]
    public async Task<ActionResult<IReadOnlyList<AuditEntryDto>>> Audit([FromQuery] AuditQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _service.ListAuditAsync(query, cancellationToken));
    }

    /// <summary>UC-4: one claim with documents, agent results, decision and history.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ClaimDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClaimDto>> Get(string id, CancellationToken cancellationToken)
    {
        return Ok(await _service.GetAsync(id, CurrentUser(), cancellationToken));
    }

    /// <summary>UC-8: history of one claim, oldest first. Customers see only the entries meant for them.</summary>
    [HttpGet("{id}/history")]
    public async Task<ActionResult<IReadOnlyList<HistoryEventDto>>> History(string id, CancellationToken cancellationToken)
    {
        return Ok(await _service.GetHistoryAsync(id, CurrentUser(), cancellationToken));
    }

    private CurrentUser CurrentUser()
    {
        return User.ToCurrentUser(_authOptions);
    }

    /// <summary>The caller's token, passed on to PolicyService.</summary>
    private string? BearerToken()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header.Substring(prefix.Length).Trim() : null;
    }
}
