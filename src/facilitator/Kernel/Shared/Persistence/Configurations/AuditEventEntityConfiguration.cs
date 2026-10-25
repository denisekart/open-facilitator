using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.audit — append-only decision spine.</summary>
public sealed class AuditEventEntityConfiguration : IEntityTypeConfiguration<AuditEventEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditEventEntity> e)
    {
        e.ToTable("audit");
        e.HasKey(x => x.AuditId);
        e.Property(x => x.AuditId).HasColumnName("audit_id").ValueGeneratedNever();
        e.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();
        e.Property(x => x.Actor).HasColumnName("actor");
        e.Property(x => x.Action).HasColumnName("action").IsRequired();
        e.Property(x => x.Resource).HasColumnName("resource");
        e.Property(x => x.Decision).HasColumnName("decision").IsRequired();
        e.Property(x => x.Reason).HasColumnName("reason");
        e.Property(x => x.EvidenceRef).HasColumnName("evidence_ref");
        e.Property(x => x.PolicyVersion).HasColumnName("policy_version");
        e.Property(x => x.Detail).HasColumnName("detail").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.TraceId).HasColumnName("trace_id");
        e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        e.HasIndex(x => x.OccurredAt).HasDatabaseName("audit_occurred_at_idx");
        e.HasIndex(x => new { x.Actor, x.OccurredAt }).HasDatabaseName("audit_actor_occurred_at_idx");
        e.ToTable(t => t.HasCheckConstraint("audit_decision_check", CheckSql.InList("decision", ["allow", "deny"])));
    }
}
