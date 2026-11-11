using System.Text.Json;
using Horica.ClaimsService.Domain;
using Horica.ClaimsService.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Horica.ClaimsService.Infrastructure.Persistence;

public class ClaimsDbContext : DbContext
{
    /// <summary>Claim numbers: CLM-1001, CLM-1002 ...</summary>
    public const string ClaimNumberSequence = "claim_number_seq";

    public const long FirstClaimNumber = 1001;

    public ClaimsDbContext(DbContextOptions<ClaimsDbContext> options)
        : base(options)
    {
    }

    public DbSet<Claim> Claims => Set<Claim>();

    public DbSet<ClaimDocument> Documents => Set<ClaimDocument>();

    public DbSet<ClaimHistoryEntry> History => Set<ClaimHistoryEntry>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(ClaimNumberSequence).StartsAt(FirstClaimNumber).IncrementsBy(1);

        var agentsComparer = new ValueComparer<List<AgentStep>>(
            (a, b) => AgentsToJson(a) == AgentsToJson(b),
            v => AgentsToJson(v).GetHashCode(),
            v => AgentsFromJson(AgentsToJson(v)));

        modelBuilder.Entity<Claim>(e =>
        {
            e.ToTable("claims");
            e.HasKey(x => x.Id);

            e.Property(x => x.Id).HasColumnName("id").HasMaxLength(100).ValueGeneratedNever();
            e.Property(x => x.CustomerId).HasColumnName("customer_id").HasMaxLength(200).IsRequired();
            e.Property(x => x.Claimant).HasColumnName("claimant").HasMaxLength(200).IsRequired();
            e.Property(x => x.PolicyNumber).HasColumnName("policy_number").HasMaxLength(100).IsRequired();
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(30);
            e.Property(x => x.IncidentDate).HasColumnName("incident_date");
            e.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2);
            e.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").IsRequired();

            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.AnalysisRound).HasColumnName("analysis_round");

            e.Property(x => x.RiskScore).HasColumnName("risk_score");
            e.Property(x => x.RiskReasons).HasColumnName("risk_reasons").HasColumnType("text[]");
            e.Property(x => x.Agents)
                .HasColumnName("agents")
                .HasColumnType("text")
                .HasConversion(v => AgentsToJson(v), v => AgentsFromJson(v), agentsComparer);
            e.Property(x => x.RecommendationAction).HasColumnName("recommendation_action").HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RecommendationSummary).HasColumnName("recommendation_summary");
            e.Property(x => x.RecommendationAutomatic).HasColumnName("recommendation_automatic");
            e.Property(x => x.AnalyzedAt).HasColumnName("analyzed_at");

            e.Property(x => x.ReviewId).HasColumnName("review_id").HasMaxLength(100);
            e.Property(x => x.ReviewDecision).HasColumnName("review_decision").HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ReviewComment).HasColumnName("review_comment");
            e.Property(x => x.ReviewerId).HasColumnName("reviewer_id").HasMaxLength(200);
            e.Property(x => x.ReviewerName).HasColumnName("reviewer_name").HasMaxLength(200);
            e.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");

            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.HistoryCount).HasColumnName("history_count");
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();

            e.HasMany(x => x.Documents)
                .WithOne()
                .HasForeignKey(d => d.ClaimId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(x => x.History)
                .WithOne()
                .HasForeignKey(h => h.ClaimId)
                .OnDelete(DeleteBehavior.Cascade);

            // Working lists of one change; never stored.
            e.Ignore(x => x.NewHistory);
            e.Ignore(x => x.NewDocuments);
            e.Ignore(x => x.NewEvents);

            e.HasIndex(x => new { x.CustomerId, x.CreatedAt }).HasDatabaseName("ix_claims_customer_created");
            e.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_claims_status_created");
            e.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_claims_created");
        });

        modelBuilder.Entity<ClaimDocument>(e =>
        {
            e.ToTable("claim_documents");
            e.HasKey(x => x.Id);

            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.ClaimId).HasColumnName("claim_id").HasMaxLength(100).IsRequired();
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
            e.Property(x => x.DocumentId).HasColumnName("document_id").HasMaxLength(200);
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
            e.Property(x => x.UploadedById).HasColumnName("uploaded_by_id").HasMaxLength(200);

            e.HasIndex(x => new { x.ClaimId, x.Position }).IsUnique().HasDatabaseName("ux_claim_documents_position");
        });

        modelBuilder.Entity<ClaimHistoryEntry>(e =>
        {
            e.ToTable("claim_history");
            e.HasKey(x => new { x.ClaimId, x.Seq });

            e.Property(x => x.ClaimId).HasColumnName("claim_id").HasMaxLength(100);
            e.Property(x => x.Seq).HasColumnName("seq").ValueGeneratedNever();
            e.Property(x => x.At).HasColumnName("at");
            e.Property(x => x.Event).HasColumnName("event").HasMaxLength(100).IsRequired();
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(200).IsRequired();
            e.Property(x => x.Text).HasColumnName("text").IsRequired();
            e.Property(x => x.CustomerVisible).HasColumnName("customer_visible");

            e.HasIndex(x => x.At).HasDatabaseName("ix_claim_history_at");
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);

            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Topic).HasColumnName("topic").HasMaxLength(200).IsRequired();
            e.Property(x => x.MessageKey).HasColumnName("message_key").HasMaxLength(200).IsRequired();
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(200).IsRequired();
            e.Property(x => x.Payload).HasColumnName("payload").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.Sequence).HasColumnName("sequence");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.LastError).HasColumnName("last_error");

            e.HasIndex(x => new { x.CreatedAt, x.Sequence })
                .HasFilter("processed_at IS NULL")
                .HasDatabaseName("ix_outbox_pending");
        });

        modelBuilder.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_events");
            e.HasKey(x => x.EventId);

            e.Property(x => x.EventId).HasColumnName("event_id").HasMaxLength(300);
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(200).IsRequired();
            e.Property(x => x.ClaimId).HasColumnName("claim_id").HasMaxLength(100);
            e.Property(x => x.Outcome).HasColumnName("outcome");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });
    }

    private static string AgentsToJson(List<AgentStep>? steps)
    {
        return JsonSerializer.Serialize(steps ?? new List<AgentStep>(), JsonDefaults.Options);
    }

    private static List<AgentStep> AgentsFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<AgentStep>();
        }

        return JsonSerializer.Deserialize<List<AgentStep>>(json, JsonDefaults.Options) ?? new List<AgentStep>();
    }
}
