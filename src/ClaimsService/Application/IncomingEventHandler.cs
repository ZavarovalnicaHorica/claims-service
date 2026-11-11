using Horica.ClaimsService.Domain;
using Horica.ClaimsService.Infrastructure.Messaging;
using Horica.ClaimsService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Horica.ClaimsService.Application;

/// <summary>
/// Applies the events ClaimsService consumes to the claims. Safe to call more than once with the same event
/// (Kafka delivers at least once): handled event ids are stored in processed_events.
/// </summary>
public sealed class IncomingEventHandler
{
    private readonly ClaimsDbContext _db;
    private readonly AutoApprovalOptions _rules;
    private readonly KafkaOptions _kafka;
    private readonly EventOptions _events;
    private readonly TimeProvider _time;
    private readonly ILogger<IncomingEventHandler> _logger;

    public IncomingEventHandler(
        ClaimsDbContext db,
        IOptions<AutoApprovalOptions> rules,
        IOptions<KafkaOptions> kafka,
        IOptions<EventOptions> events,
        TimeProvider time,
        ILogger<IncomingEventHandler> logger)
    {
        _db = db;
        _rules = rules.Value;
        _kafka = kafka.Value;
        _events = events.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Parses a Kafka message by its topic and applies it.</summary>
    public Task<ApplyResult> HandleAsync(string topic, string? json, string fallbackEventId, CancellationToken cancellationToken)
    {
        if (topic == _kafka.RecommendationCreatedTopic)
        {
            return ApplyRecommendationAsync(IncomingEventParsers.ParseRecommendation(json, fallbackEventId), cancellationToken);
        }

        if (topic == _kafka.ReviewCompletedTopic)
        {
            return ApplyReviewAsync(IncomingEventParsers.ParseReviewCompleted(json, fallbackEventId), cancellationToken);
        }

        if (topic == _kafka.DocumentAddedTopic)
        {
            return ApplyDocumentsAsync(IncomingEventParsers.ParseDocumentAdded(json, fallbackEventId), cancellationToken);
        }

        throw new InvalidMessageException("ClaimsService does not handle topic '" + topic + "'.");
    }

    public Task<ApplyResult> ApplyRecommendationAsync(Parsed<RecommendationInput> parsed, CancellationToken cancellationToken)
    {
        return ApplyAsync(
            parsed.EventId,
            ClaimEvents.RecommendationCreatedType,
            parsed.Data.ClaimId,
            (claim, now) => claim.ApplyRecommendation(parsed.Data, _rules, now),
            cancellationToken);
    }

    public Task<ApplyResult> ApplyReviewAsync(Parsed<ReviewResultInput> parsed, CancellationToken cancellationToken)
    {
        return ApplyAsync(
            parsed.EventId,
            ClaimEvents.ReviewCompletedType,
            parsed.Data.ClaimId,
            (claim, now) => claim.ApplyReview(parsed.Data, now),
            cancellationToken);
    }

    public Task<ApplyResult> ApplyDocumentsAsync(Parsed<DocumentAddedInput> parsed, CancellationToken cancellationToken)
    {
        return ApplyAsync(
            parsed.EventId,
            ClaimEvents.DocumentAddedType,
            parsed.Data.ClaimId,
            (claim, now) => claim.AddDocuments(parsed.Data, now),
            cancellationToken);
    }

    private async Task<ApplyResult> ApplyAsync(
        string eventId,
        string eventType,
        string claimId,
        Func<Claim, DateTimeOffset, ApplyResult> apply,
        CancellationToken cancellationToken)
    {
        var alreadyProcessed = await _db.ProcessedEvents.AnyAsync(e => e.EventId == eventId, cancellationToken);
        if (alreadyProcessed)
        {
            _logger.LogInformation("Event {EventId} was already processed; ignoring the duplicate.", eventId);
            return ApplyResult.Ignored("Event " + eventId + " was already processed.");
        }

        var now = _time.GetUtcNow();

        var claim = await _db.Claims
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken);

        ApplyResult result;
        if (claim is null)
        {
            result = ApplyResult.Ignored("Claim " + claimId + " does not exist.");
        }
        else
        {
            result = apply(claim, now);
            ClaimChanges.Stage(_db, claim, _kafka, _events.Source, now);
        }

        _db.ProcessedEvents.Add(new ProcessedEvent
        {
            EventId = eventId,
            EventType = eventType,
            ClaimId = claimId,
            Outcome = (result.Applied ? "Applied: " : "Ignored: ") + result.Note,
            ProcessedAt = now
        });

        await _db.SaveChangesAsync(cancellationToken);

        if (result.Applied)
        {
            _logger.LogInformation("{EventType} {EventId} for claim {ClaimId}: {Note}", eventType, eventId, claimId, result.Note);
        }
        else
        {
            _logger.LogWarning("{EventType} {EventId} for claim {ClaimId} ignored: {Note}", eventType, eventId, claimId, result.Note);
        }

        return result;
    }
}
