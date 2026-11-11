namespace Horica.ClaimsService.Domain;

/// <summary>
/// Something other services must hear about. The claim collects these while it changes;
/// the application turns them into outbox rows (Kafka events) in the same database transaction.
/// </summary>
public abstract record ClaimDomainEvent;

/// <summary>claim.submitted.v1: the agents should (re)analyse the claim.</summary>
public sealed record ClaimSubmittedEvent(bool Reanalysis) : ClaimDomainEvent;

/// <summary>review.requested.v1: an employee has to decide. Reasons say why it was not approved automatically.</summary>
public sealed record ReviewRequestedEvent(IReadOnlyList<string> Reasons) : ClaimDomainEvent;

/// <summary>claim.status.changed.v1: the status moved from Previous to Current.</summary>
public sealed record ClaimStatusChangedEvent(ClaimStatus Previous, ClaimStatus Current, string? Reason) : ClaimDomainEvent;
