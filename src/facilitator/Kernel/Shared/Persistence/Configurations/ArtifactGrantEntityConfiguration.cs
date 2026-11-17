using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.artifact_grants — delivery-scoped read rentals (rent, don't own).
/// The (artifact_id, command_id) unique pair is the replay guard for a re-appended
/// reply command; only <c>active</c> rows are ever consultable and there is no
/// read-by-listing route — a grant is neither queryable nor enumerable from the
/// link plane.</summary>
public sealed class ArtifactGrantEntityConfiguration : IEntityTypeConfiguration<ArtifactGrantEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ArtifactGrantEntity> e)
    {
        e.ToTable("artifact_grants");
        e.HasKey(x => x.GrantId);
        e.Property(x => x.GrantId).HasColumnName("grant_id").ValueGeneratedNever();
        e.Property(x => x.ArtifactId).HasColumnName("artifact_id").IsRequired();
        e.Property(x => x.GranteeTeamSlug).HasColumnName("grantee_team_slug").IsRequired();
        e.Property(x => x.GranteeEndpointId).HasColumnName("grantee_endpoint_id").IsRequired();
        e.Property(x => x.CommandId).HasColumnName("command_id").IsRequired();
        e.Property(x => x.Status).HasColumnName("status").IsRequired();
        e.Property(x => x.MintedBy).HasColumnName("minted_by").IsRequired();
        e.Property(x => x.Reason).HasColumnName("reason");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        e.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
        e.Property(x => x.ConsumeReason).HasColumnName("consume_reason");
        // Replay guard: a re-appended reply command re-mints nothing.
        e.HasIndex(x => new { x.ArtifactId, x.CommandId }).IsUnique()
            .HasDatabaseName("artifact_grants_artifact_command_unique");
        e.HasIndex(x => new { x.ArtifactId, x.GranteeEndpointId })
            .HasDatabaseName("artifact_grants_open_idx")
            .HasFilter("status = 'active'");
        e.ToTable(t => t.HasCheckConstraint("artifact_grants_status_check",
            CheckSql.InList("status", ["active", "consumed", "revoked", "expired"])));
        e.ToTable(t => t.HasCheckConstraint("artifact_grants_consume_reason_check",
            CheckSql.InList("consume_reason", ["read", "receipt", "expiry"])));
        e.HasOne<ArtifactEntity>().WithMany().HasForeignKey(x => x.ArtifactId)
            .HasConstraintName("artifact_grants_artifact_id_fkey").OnDelete(DeleteBehavior.Cascade);
    }
}
