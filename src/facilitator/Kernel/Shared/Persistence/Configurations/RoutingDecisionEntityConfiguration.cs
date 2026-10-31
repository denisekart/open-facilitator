using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.routing_decisions — routing funnel audit.</summary>
public sealed class RoutingDecisionEntityConfiguration : IEntityTypeConfiguration<RoutingDecisionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RoutingDecisionEntity> e)
    {
        e.ToTable("routing_decisions");
        e.HasKey(x => x.DecisionId);
        e.Property(x => x.DecisionId).HasColumnName("decision_id").ValueGeneratedNever();
        e.Property(x => x.WorkId).HasColumnName("work_id").IsRequired();
        e.Property(x => x.IntentCommandId).HasColumnName("intent_command_id").IsRequired();
        e.Property(x => x.CapabilityId).HasColumnName("capability_id").IsRequired();
        e.Property(x => x.TeamSlug).HasColumnName("team_slug");
        e.Property(x => x.AllowedCandidates).HasColumnName("allowed_candidates").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.ChosenEndpointId).HasColumnName("chosen_endpoint_id");
        e.Property(x => x.Status).HasColumnName("status").IsRequired();
        e.Property(x => x.Basis).HasColumnName("basis").IsRequired();
        e.Property(x => x.ProviderRef).HasColumnName("provider_ref");
        e.Property(x => x.LastRoutingAttemptAt).HasColumnName("last_routing_attempt_at").IsRequired();
        e.Property(x => x.AttemptCount).HasColumnName("attempt_count").IsRequired();
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        e.HasIndex(x => x.IntentCommandId).IsUnique().HasDatabaseName("routing_decisions_intent_unique");
        e.HasIndex(x => new { x.Status, x.LastRoutingAttemptAt })
            .HasDatabaseName("routing_decisions_pending")
            .HasFilter("status = 'pending'");
        e.HasOne<WorkEntity>().WithMany().HasForeignKey(x => x.WorkId).HasConstraintName("routing_decisions_work_id_fkey");
        e.ToTable(t => t.HasCheckConstraint("routing_decisions_status_check",
            CheckSql.InList("status", ["pending", "routed", "abandoned"])));
    }
}
