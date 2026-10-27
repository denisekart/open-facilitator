using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.result_manifests — immutable result-manifest ledger (Plan 15).</summary>
public sealed class ResultManifestEntityConfiguration : IEntityTypeConfiguration<ResultManifestEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ResultManifestEntity> e)
    {
        e.ToTable("result_manifests");
        e.HasKey(x => x.ManifestId);
        e.Property(x => x.ManifestId).HasColumnName("manifest_id").ValueGeneratedNever();
        e.Property(x => x.WorkId).HasColumnName("work_id").IsRequired();
        e.Property(x => x.AssignmentId).HasColumnName("assignment_id").IsRequired();
        e.Property(x => x.ExecutionId).HasColumnName("execution_id").IsRequired();
        e.Property(x => x.Revision).HasColumnName("revision").IsRequired();
        e.Property(x => x.RevisionOf).HasColumnName("revision_of");
        e.Property(x => x.Digest).HasColumnName("digest").IsRequired();
        e.Property(x => x.StructuredDigest).HasColumnName("structured_digest");
        e.Property(x => x.ArtifactId).HasColumnName("artifact_id").IsRequired();
        e.Property(x => x.OutputArtifactIds).HasColumnName("output_artifact_ids").HasColumnType("text[]").IsRequired()
            .HasDefaultValue(Array.Empty<string>());
        e.Property(x => x.ResultState).HasColumnName("result_state").IsRequired();
        e.Property(x => x.State).HasColumnName("state").IsRequired();
        e.Property(x => x.RecordedAt).HasColumnName("recorded_at").IsRequired();
        e.HasIndex(x => x.WorkId).HasDatabaseName("ix_result_manifests_work_id");
        e.HasIndex(x => x.AssignmentId).HasDatabaseName("ix_result_manifests_assignment_id");
        e.HasIndex(x => new { x.AssignmentId, x.Revision }).IsUnique()
            .HasDatabaseName("result_manifests_assignment_id_revision_key");
        e.HasOne<WorkEntity>().WithMany().HasForeignKey(x => x.WorkId)
            .HasConstraintName("result_manifests_work_id_fkey");
        // Self-FK revision chain: a plain FK to manifest_id, no navigation (the chain is read
        // explicitly; no consumer needs the loaded graph). The FK index is named by hand to keep
        // the ix_<table>_<col> law (EF would auto-name it IX_...).
        e.HasOne<ResultManifestEntity>().WithMany().HasForeignKey(x => x.RevisionOf)
            .HasConstraintName("result_manifests_revision_of_fkey");
        e.HasIndex(x => x.RevisionOf).HasDatabaseName("ix_result_manifests_revision_of");
        e.ToTable(t => t.HasCheckConstraint("result_manifests_digest_check",
            CheckSql.IsSha256Digest("digest")));
        e.ToTable(t => t.HasCheckConstraint("result_manifests_result_state_check",
            CheckSql.InList("result_state", ["missing", "available", "validated", "rejected", "superseded"])));
        e.ToTable(t => t.HasCheckConstraint("result_manifests_state_check",
            CheckSql.InList("state", ["recorded", "superseded"])));
    }
}
