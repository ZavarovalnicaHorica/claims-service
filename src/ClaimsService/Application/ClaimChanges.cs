using Horica.ClaimsService.Domain;
using Horica.ClaimsService.Infrastructure.Messaging;
using Horica.ClaimsService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Horica.ClaimsService.Application;

public static class ClaimChanges
{
    /// <summary>
    /// Adds what the claim produced during one change (history, new documents, outbox events) to the context,
    /// so a single SaveChanges stores the change, its history and its events in one transaction.
    /// </summary>
    public static void Stage(ClaimsDbContext db, Claim claim, KafkaOptions kafka, string source, DateTimeOffset now)
    {
        foreach (var document in claim.NewDocuments)
        {
            if (db.Entry(document).State == EntityState.Detached)
            {
                db.Documents.Add(document);
            }
        }

        db.History.AddRange(claim.NewHistory);
        db.Outbox.AddRange(ClaimEvents.ToOutbox(claim, kafka, source, now));

        claim.NewDocuments.Clear();
        claim.NewHistory.Clear();
        claim.NewEvents.Clear();
    }
}
