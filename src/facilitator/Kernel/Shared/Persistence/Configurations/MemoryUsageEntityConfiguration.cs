using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.memory_usage.</summary>
public sealed class MemoryUsageEntityConfiguration : IEntityTypeConfiguration<MemoryUsageEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MemoryUsageEntity> e)
    {
        e.ToTable("memory_usage");
        e.HasKey(x => x.UsageId);
        e.Property(x => x.UsageId).HasColumnName("usage_id").ValueGeneratedNever();
        e.Property(x => x.EntryId).HasColumnName("entry_id").IsRequired();
        e.Property(x => x.Consumer).HasColumnName("consumer").IsRequired();
        e.Property(x => x.Purpose).HasColumnName("purpose").IsRequired();
        e.Property(x => x.Justification).HasColumnName("justification").IsRequired();
        e.Property(x => x.WorkId).HasColumnName("work_id");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.HasIndex(x => x.EntryId).HasDatabaseName("ix_memory_usage_entry");
    }
}
