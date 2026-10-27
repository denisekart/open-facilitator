using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.artifacts — durable artifact metadata ledger.</summary>
public sealed class ArtifactEntityConfiguration : IEntityTypeConfiguration<ArtifactEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ArtifactEntity> e)
    {
        e.ToTable("artifacts");
        e.HasKey(x => x.ArtifactId);
        e.Property(x => x.ArtifactId).HasColumnName("artifact_id").ValueGeneratedNever();
        e.Property(x => x.WorkId).HasColumnName("work_id");
        e.Property(x => x.AssignmentId).HasColumnName("assignment_id");
        e.Property(x => x.MediaType).HasColumnName("media_type").IsRequired();
        e.Property(x => x.Size).HasColumnName("size").IsRequired();
        e.Property(x => x.Digest).HasColumnName("digest").IsRequired();
        e.Property(x => x.StorageUri).HasColumnName("storage_uri").IsRequired();
        e.Property(x => x.State).HasColumnName("state").IsRequired();
        e.Property(x => x.Verification).HasColumnName("verification").IsRequired();
        e.Property(x => x.Origin).HasColumnName("origin").IsRequired();
        e.Property(x => x.CreatedBy).HasColumnName("created_by").IsRequired();
        e.Property(x => x.TeamSlug).HasColumnName("team_slug");
        e.Property(x => x.RequestedByEndpointId).HasColumnName("requested_by_endpoint_id");
        e.Property(x => x.CommandId).HasColumnName("command_id");
        e.Property(x => x.WireArtifactId).HasColumnName("wire_artifact_id");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        e.Property(x => x.StoredAt).HasColumnName("stored_at");
        e.Property(x => x.RetentionPolicy).HasColumnName("retention_policy").IsRequired();
        e.HasIndex(x => x.WorkId).HasDatabaseName("ix_artifacts_work_id");
        e.HasIndex(x => x.Digest).HasDatabaseName("ix_artifacts_digest");
        // Content-addressed dedupe makes duplicate digests legal across works —
        // a plain index, never unique on digest.
        e.HasIndex(x => x.CommandId).HasDatabaseName("ix_artifacts_command_id");
        e.HasIndex(x => x.WireArtifactId).HasDatabaseName("ix_artifacts_wire_artifact_id");
        e.ToTable(t => t.HasCheckConstraint("artifacts_state_check",
            CheckSql.InList("state", ["pending", "stored", "rejected", "deleted"])));
        e.ToTable(t => t.HasCheckConstraint("artifacts_verification_check",
            CheckSql.InList("verification", ["verified", "unverified"])));
        e.ToTable(t => t.HasCheckConstraint("artifacts_origin_check",
            CheckSql.InList("origin", ["link", "rest", "external"])));
    }
}
