using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.work_links — typed work-to-work relations.</summary>
public sealed class WorkLinkEntityConfiguration : IEntityTypeConfiguration<WorkLinkEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WorkLinkEntity> e)
    {
        e.ToTable("work_links");
        e.HasKey(x => x.LinkId);
        e.Property(x => x.LinkId).HasColumnName("link_id").ValueGeneratedNever();
        e.Property(x => x.WorkId).HasColumnName("work_id").IsRequired();
        e.Property(x => x.RefWorkId).HasColumnName("ref_work_id").IsRequired();
        e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
        e.Property(x => x.Note).HasColumnName("note");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.HasIndex(x => new { x.WorkId, x.RefWorkId, x.Kind }).IsUnique().HasDatabaseName("work_links_unique");
        e.HasIndex(x => x.RefWorkId).HasDatabaseName("work_links_ref_idx");
        e.HasOne(x => x.Work).WithMany().HasForeignKey(x => x.WorkId).HasConstraintName("work_links_work_id_fkey");
        e.HasOne<WorkEntity>().WithMany().HasForeignKey(x => x.RefWorkId).HasConstraintName("work_links_ref_work_id_fkey");
        e.ToTable(t => t.HasCheckConstraint("work_links_kind_check",
            CheckSql.InList("kind", ["successor", "parent", "child", "merged-duplicate", "context"])));
    }
}
