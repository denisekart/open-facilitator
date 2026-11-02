using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>
/// facilitator.memory_entries. The <c>search</c> tsvector generated
/// column and its GIN index are raw SQL in the <c>AddMemoryBroker</c>
/// migration — deliberately absent from the model: the broker never reads or
/// writes the column, and Postgres recomputes it when tombstoning empties
/// <c>content</c> (D5), so no trigger plumbing and no snapshot churn.
/// </summary>
public sealed class MemoryEntryEntityConfiguration : IEntityTypeConfiguration<MemoryEntryEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MemoryEntryEntity> e)
    {
        e.ToTable("memory_entries");
        e.HasKey(x => x.EntryId);
        e.Property(x => x.EntryId).HasColumnName("entry_id").ValueGeneratedNever();
        e.Property(x => x.ScopeId).HasColumnName("scope_id").IsRequired();
        e.Property(x => x.TeamSlug).HasColumnName("team_slug").IsRequired();
        e.Property(x => x.Type).HasColumnName("type").IsRequired();
        e.Property(x => x.Content).HasColumnName("content").IsRequired();
        e.Property(x => x.MediaType).HasColumnName("media_type").IsRequired();
        e.Property(x => x.ContentSummary).HasColumnName("content_summary").IsRequired();
        e.Property(x => x.ContentHash).HasColumnName("content_hash").IsRequired();
        e.Property(x => x.ProvenanceJson).HasColumnName("provenance").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.ObservedAt).HasColumnName("observed_at").IsRequired();
        e.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
        e.Property(x => x.Classification).HasColumnName("classification").IsRequired();
        e.Property(x => x.PolicyLabel).HasColumnName("policy_label").IsRequired();
        e.Property(x => x.Status).HasColumnName("status").IsRequired();
        e.Property(x => x.Revision).HasColumnName("revision").IsRequired();
        e.Property(x => x.SupersedesEntryId).HasColumnName("supersedes_entry_id");
        e.Property(x => x.ContradictsEntryIds).HasColumnName("contradicts_entry_ids").HasColumnType("text[]").IsRequired();
        e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        e.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_memory_entries_expires").HasFilter("status <> 'tombstoned'");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.CreatedBy).HasColumnName("created_by").IsRequired();
        e.HasIndex(x => x.TeamSlug).HasDatabaseName("ix_memory_entries_team");
        e.HasIndex(x => x.ScopeId).HasDatabaseName("ix_memory_entries_scope");
        e.HasOne<MemoryScopeEntity>().WithMany().HasForeignKey(x => x.ScopeId).HasConstraintName("memory_entries_scope_id_fkey");
        e.ToTable(t => t.HasCheckConstraint("memory_entries_type_check",
            CheckSql.InList("type", ["observed_fact", "experience", "decision", "lesson_candidate", "lesson_verified", "hypothesis", "preference"])));
        e.ToTable(t => t.HasCheckConstraint("memory_entries_classification_check",
            CheckSql.InList("classification", ["internal", "public"])));
        e.ToTable(t => t.HasCheckConstraint("memory_entries_policy_label_check",
            CheckSql.InList("policy_label", ["private", "team-only", "facilitator-visible", "evidence-only"])));
        e.ToTable(t => t.HasCheckConstraint("memory_entries_status_check",
            CheckSql.InList("status", ["candidate", "active", "superseded", "tombstoned"])));
        e.ToTable(t => t.HasCheckConstraint("memory_entries_content_hash_check",
            CheckSql.IsSha256Digest("content_hash")));
    }
}
