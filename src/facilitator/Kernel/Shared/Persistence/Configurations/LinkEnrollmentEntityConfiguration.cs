using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.link_enrollments — appendix §4 enrollment records.</summary>
public sealed class LinkEnrollmentEntityConfiguration : IEntityTypeConfiguration<LinkEnrollmentEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LinkEnrollmentEntity> e)
    {
        e.ToTable("link_enrollments");
        e.HasKey(x => x.EnrollmentId);
        e.Property(x => x.EnrollmentId).HasColumnName("enrollment_id").ValueGeneratedNever();
        e.Property(x => x.EndpointId).HasColumnName("endpoint_id").IsRequired();
        e.Property(x => x.TeamId).HasColumnName("team_id").IsRequired();
        e.Property(x => x.TokenSha256).HasColumnName("token_sha256").IsRequired();
        e.Property(x => x.TrustMode).HasColumnName("trust_mode").IsRequired();
        e.Property(x => x.CapabilityManifest).HasColumnName("capability_manifest").HasColumnType("jsonb");
        e.Property(x => x.CapabilitiesDigest).HasColumnName("capabilities_digest");
        e.Property(x => x.EnrolledAt).HasColumnName("enrolled_at").IsRequired();
        e.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        e.HasIndex(x => x.EndpointId).IsUnique().HasDatabaseName("link_enrollments_endpoint_id_key");
        e.HasOne<TeamEntity>().WithMany().HasForeignKey(x => x.TeamId).HasConstraintName("link_enrollments_team_id_fkey");
        e.ToTable(t => t.HasCheckConstraint("link_enrollments_trust_mode_check",
            CheckSql.InList("trust_mode", ["shared-runtime", "separate-process-same-host", "isolated-executor"])));
    }
}
