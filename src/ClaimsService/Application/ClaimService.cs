using Horica.ClaimsService.Auth;
using Horica.ClaimsService.Common;
using Horica.ClaimsService.Domain;
using Horica.ClaimsService.Infrastructure.Messaging;
using Horica.ClaimsService.Infrastructure.Persistence;
using Horica.ClaimsService.Infrastructure.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Horica.ClaimsService.Application;

/// <summary>UC-2 (submit), UC-4 (track status) and UC-8 (history) of ClaimsService.</summary>
public sealed class ClaimService
{
    public const int MaxDocumentsPerRequest = 20;
    public const int MaxDocumentNameLength = 255;
    public const decimal MaxAmount = 100_000_000m;

    private static readonly HashSet<string> KnownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CAR", "HOME", "TRAVEL", "HEALTH"
    };

    private readonly ClaimsDbContext _db;
    private readonly IClaimIdGenerator _ids;
    private readonly PolicyClient _policies;
    private readonly PolicyOptions _policyOptions;
    private readonly KafkaOptions _kafka;
    private readonly EventOptions _events;
    private readonly TimeProvider _time;
    private readonly ILogger<ClaimService> _logger;

    public ClaimService(
        ClaimsDbContext db,
        IClaimIdGenerator ids,
        PolicyClient policies,
        IOptions<PolicyOptions> policyOptions,
        IOptions<KafkaOptions> kafka,
        IOptions<EventOptions> events,
        TimeProvider time,
        ILogger<ClaimService> logger)
    {
        _db = db;
        _ids = ids;
        _policies = policies;
        _policyOptions = policyOptions.Value;
        _kafka = kafka.Value;
        _events = events.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Stores the claim and, in the same transaction, claim.submitted.v1 (outbox). Status: SUBMITTED.
    /// </summary>
    public async Task<ClaimDto> SubmitAsync(
        SubmitClaimRequest request,
        CurrentUser user,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var input = Validate(request, now);

        var type = input.Type;
        var lookup = await _policies.LookupAsync(input.PolicyNumber, bearerToken, cancellationToken);

        if (lookup.Status == PolicyLookupStatus.NotFound)
        {
            throw new InvalidRequestException("Policy " + input.PolicyNumber + " was not found. Choose one of your policies.");
        }

        if (lookup.Status == PolicyLookupStatus.Found && lookup.Policy is not null)
        {
            if (_policyOptions.RequireHolderMatch &&
                lookup.Policy.HolderId is not null &&
                !string.Equals(lookup.Policy.HolderId, user.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidRequestException("Policy " + input.PolicyNumber + " belongs to another customer. Choose one of your policies.");
            }

            type = lookup.Policy.Type ?? type;
        }

        var id = await _ids.NextAsync(cancellationToken);

        var claim = Claim.Submit(
            new NewClaimData(
                Id: id,
                CustomerId: user.Id,
                Claimant: user.DisplayName,
                PolicyNumber: input.PolicyNumber,
                Type: type,
                IncidentDate: input.IncidentDate,
                Amount: input.Amount,
                Currency: "EUR",
                Description: input.Description,
                Documents: input.Documents),
            now);

        _db.Claims.Add(claim);
        ClaimChanges.Stage(_db, claim, _kafka, _events.Source, now);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Claim {ClaimId} submitted by {CustomerId} for {Amount} EUR on policy {PolicyNumber}.",
            claim.Id,
            claim.CustomerId,
            claim.Amount,
            claim.PolicyNumber);

        return await GetAsync(claim.Id, user, cancellationToken);
    }

    /// <summary>Customers get their own claims, employees every claim. Newest first.</summary>
    public async Task<IReadOnlyList<ClaimDto>> ListAsync(ClaimListQuery query, CurrentUser user, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit, 1, 1000);
        var staffView = EnsureCanRead(user);

        IQueryable<Claim> claims = _db.Claims.AsNoTracking();

        if (!staffView)
        {
            var me = user.Id;
            claims = claims.Where(c => c.CustomerId == me);
        }
        else if (!string.IsNullOrWhiteSpace(query.CustomerId))
        {
            var customerId = query.CustomerId.Trim();
            claims = claims.Where(c => c.CustomerId == customerId);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!ClaimStatusExtensions.TryParse(query.Status, out var status))
            {
                throw new InvalidRequestException(
                    "Unknown status '" + query.Status + "'. Use SUBMITTED, NEEDS_REVIEW, NEEDS_INFO, APPROVED or REJECTED.");
            }

            claims = claims.Where(c => c.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.PolicyNumber))
        {
            var policyNumber = query.PolicyNumber.Trim();
            claims = claims.Where(c => c.PolicyNumber == policyNumber);
        }

        var list = await claims
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(limit)
            .Include(c => c.Documents)
            .Include(c => c.History)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return list.Select(c => c.ToDto(staffView)).ToList();
    }

    /// <summary>404 also when the claim belongs to another customer, so ids of others cannot be probed.</summary>
    public async Task<ClaimDto> GetAsync(string id, CurrentUser user, CancellationToken cancellationToken)
    {
        var claim = await LoadVisibleAsync(id, user, cancellationToken);
        return claim.ToDto(user.IsStaff);
    }

    /// <summary>History of one claim, oldest first. Customers see only the entries meant for them.</summary>
    public async Task<IReadOnlyList<HistoryEventDto>> GetHistoryAsync(string id, CurrentUser user, CancellationToken cancellationToken)
    {
        var claim = await LoadVisibleAsync(id, user, cancellationToken);
        return claim.ToDto(user.IsStaff).History;
    }

    /// <summary>UC-8: every history entry of every claim, newest first (managers).</summary>
    public async Task<IReadOnlyList<AuditEntryDto>> ListAuditAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit, 1, 5000);
        IQueryable<ClaimHistoryEntry> entries = _db.History.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.ClaimId))
        {
            var claimId = query.ClaimId.Trim();
            entries = entries.Where(h => h.ClaimId == claimId);
        }

        if (!string.IsNullOrWhiteSpace(query.Event))
        {
            var eventName = query.Event.Trim();
            entries = entries.Where(h => h.Event == eventName);
        }

        var list = await entries
            .OrderByDescending(h => h.At)
            .ThenByDescending(h => h.ClaimId)
            .ThenByDescending(h => h.Seq)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return list.Select(h => new AuditEntryDto(h.ClaimId, h.At, h.Event, h.Actor, h.Text)).ToList();
    }

    private async Task<Claim> LoadVisibleAsync(string id, CurrentUser user, CancellationToken cancellationToken)
    {
        var staffView = EnsureCanRead(user);
        var claimId = (id ?? string.Empty).Trim();

        var claim = await _db.Claims
            .AsNoTracking()
            .Include(c => c.Documents)
            .Include(c => c.History)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken);

        if (claim is null || (!staffView && claim.CustomerId != user.Id))
        {
            throw new NotFoundException("Claim " + claimId + " was not found.");
        }

        return claim;
    }

    /// <summary>True for the employee view, false for the customer view.</summary>
    private static bool EnsureCanRead(CurrentUser user)
    {
        if (user.IsStaff)
        {
            return true;
        }

        if (user.IsCustomer)
        {
            return false;
        }

        throw new ForbiddenException("Your role may not read claims.");
    }

    private sealed record ValidClaim(
        string PolicyNumber,
        DateOnly IncidentDate,
        decimal Amount,
        string Description,
        IReadOnlyList<string> Documents,
        string? Type);

    private static ValidClaim Validate(SubmitClaimRequest request, DateTimeOffset now)
    {
        var policyNumber = request.PolicyNumber?.Trim();
        if (string.IsNullOrEmpty(policyNumber))
        {
            throw new InvalidRequestException("'policyNumber' is required.");
        }

        if (request.IncidentDate is null)
        {
            throw new InvalidRequestException("'incidentDate' is required.");
        }

        var incidentDate = request.IncidentDate.Value;
        // One day of slack for customers in time zones ahead of UTC.
        var latest = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
        if (incidentDate > latest)
        {
            throw new InvalidRequestException("'incidentDate' cannot be in the future.");
        }

        if (incidentDate < new DateOnly(1900, 1, 1))
        {
            throw new InvalidRequestException("'incidentDate' is not a valid date.");
        }

        if (request.Amount is null || request.Amount.Value <= 0m)
        {
            throw new InvalidRequestException("'amount' must be more than 0.");
        }

        if (request.Amount.Value > MaxAmount)
        {
            throw new InvalidRequestException("'amount' is too large.");
        }

        var amount = Math.Round(request.Amount.Value, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0m)
        {
            throw new InvalidRequestException("'amount' must be at least 0.01.");
        }

        var description = request.Description?.Trim();
        if (string.IsNullOrEmpty(description))
        {
            throw new InvalidRequestException("'description' is required.");
        }

        var documents = new List<string>();
        foreach (var raw in request.Documents ?? new List<string>())
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (name.Length > MaxDocumentNameLength)
            {
                throw new InvalidRequestException("Document names can have at most " + MaxDocumentNameLength + " characters.");
            }

            if (!documents.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                documents.Add(name);
            }
        }

        if (documents.Count > MaxDocumentsPerRequest)
        {
            throw new InvalidRequestException("At most " + MaxDocumentsPerRequest + " documents can be listed with a claim.");
        }

        string? type = null;
        if (!string.IsNullOrWhiteSpace(request.Type))
        {
            type = request.Type.Trim().ToUpperInvariant();
            if (!KnownTypes.Contains(type))
            {
                throw new InvalidRequestException("'type' must be CAR, HOME, TRAVEL or HEALTH.");
            }
        }

        return new ValidClaim(policyNumber, incidentDate, amount, description, documents, type);
    }
}
