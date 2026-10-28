using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.memory_shares (Plan 16).</summary>
public sealed class MemoryShareEntityConfiguration : IEntityTypeConfiguration<MemoryShareEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MemoryShareEntity> e)
    {
        e.ToTable("memory_shares");
        e.HasKey(x => x.ShareId);
        e.Property(x => x.ShareId).HasColumnName("share_id").ValueGeneratedNever();
        e.Property(x => x.EntryId).HasColumnName("entry_id").IsRequired();
        e.Property(x => x.GranteeTeamSlug).HasColumnName("grantee_team_slug").IsRequired();
        e.Property(x => x.GrantedBy).HasColumnName("granted_by").IsRequired();
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        // Revocation tombstones the row; a re-grant clears RevokedAt — a pair
        // never has duplicate rows.
        e.HasIndex(x => new { x.EntryId, x.GranteeTeamSlug }).IsUnique().HasDatabaseName("ix_memory_shares_pair");
        e.HasOne<MemoryEntryEntity>().WithMany().HasForeignKey(x => x.EntryId).HasConstraintName("memory_shares_entry_id_fkey");
    }
}
