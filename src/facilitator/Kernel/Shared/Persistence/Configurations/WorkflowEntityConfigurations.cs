using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>facilitator.workflow_definitions + workflow_instances + workflow_step_log.</summary>
public sealed class WorkflowEntityConfigurations :
    IEntityTypeConfiguration<WorkflowDefinitionEntity>,
    IEntityTypeConfiguration<WorkflowInstanceEntity>,
    IEntityTypeConfiguration<WorkflowStepLogEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WorkflowDefinitionEntity> e)
    {
        e.ToTable("workflow_definitions");
        e.HasKey(x => new { x.DefId, x.Version });
        e.Property(x => x.DefId).HasColumnName("def_id").HasMaxLength(128).IsRequired();
        e.Property(x => x.Version).HasColumnName("version").IsRequired();
        e.Property(x => x.Title).HasColumnName("title").HasMaxLength(120).IsRequired();
        e.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000).IsRequired();
        e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        e.Property(x => x.Digest).HasColumnName("digest").IsRequired();
        e.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        e.Property(x => x.Shadow).HasColumnName("shadow").IsRequired();
        e.Property(x => x.Source).HasColumnName("source").IsRequired();
        e.Property(x => x.Team).HasColumnName("team").HasMaxLength(128);
        e.Property(x => x.NextTickAt).HasColumnName("next_tick_at");
        e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        e.Property(x => x.CreatedBy).HasColumnName("created_by").IsRequired();
        e.ToTable(t => t.HasCheckConstraint("workflow_definitions_source_check",
            CheckSql.InList("source", ["gallery", "hand", "ai", "derived"])));
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WorkflowInstanceEntity> e)
    {
        e.ToTable("workflow_instances");
        e.HasKey(x => x.InstanceId);
        e.Property(x => x.InstanceId).HasColumnName("id").ValueGeneratedNever();
        e.Property(x => x.DefId).HasColumnName("def_id").HasMaxLength(128).IsRequired();
        e.Property(x => x.DefVersion).HasColumnName("def_version").IsRequired();
        e.Property(x => x.Team).HasColumnName("team").HasMaxLength(128);
        e.Property(x => x.ProjectId).HasColumnName("project");
        e.Property(x => x.PrimaryWorkId).HasColumnName("primary_work_id");
        e.Property(x => x.TriggerEventId).HasColumnName("trigger_event_id");
        e.Property(x => x.Status).HasColumnName("status").IsRequired();
        e.Property(x => x.CurrentStep).HasColumnName("current_step").HasMaxLength(64);
        e.Property(x => x.DeadlineAt).HasColumnName("deadline_at");
        e.Property(x => x.StartedAt).HasColumnName("started_at").IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        e.Property(x => x.ParentInstanceId).HasColumnName("parent_instance_id");
        e.Property(x => x.Depth).HasColumnName("depth").IsRequired();
        e.Property(x => x.InvocationPath).HasColumnName("invocation_path").HasMaxLength(4096).IsRequired();
        e.Property(x => x.Output).HasColumnName("output").HasColumnType("jsonb");
        e.Property(x => x.EndReason).HasColumnName("end_reason").HasMaxLength(64);
        e.ToTable(t =>
        {
            t.HasCheckConstraint("workflow_instances_status_check",
                CheckSql.InList("status", ["running", "waiting", "paused", "completed", "failed", "cancelled", "shadowed"]));
            t.HasCheckConstraint("workflow_instances_end_reason_check",
                CheckSql.InList("end_reason",
                    ["budget-exhausted", "chain-age-exceeded", "parent-cancelled", "invoke-timeout", "operator-cancel"])
                + " OR end_reason IS NULL");
        });
        e.HasIndex(x => new { x.Status, x.DeadlineAt }).HasDatabaseName("workflow_instances_status_deadline_at_idx");
        e.HasIndex(x => x.DefId).HasDatabaseName("workflow_instances_def_id_idx");
        e.HasIndex(x => x.ParentInstanceId).HasDatabaseName("workflow_instances_parent_instance_id_idx");
        e.HasIndex(x => x.PrimaryWorkId).HasDatabaseName("workflow_instances_primary_work_id_idx");
        e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentInstanceId)
            .HasConstraintName("workflow_instances_parent_instance_id_fkey");
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WorkflowStepLogEntity> e)
    {
        e.ToTable("workflow_step_log");
        e.HasKey(x => new { x.InstanceId, x.Seq });
        e.Property(x => x.InstanceId).HasColumnName("instance_id").IsRequired();
        e.Property(x => x.Seq).HasColumnName("seq").IsRequired();
        e.Property(x => x.StepId).HasColumnName("step_id").HasMaxLength(64).IsRequired();
        e.Property(x => x.Status).HasColumnName("status").IsRequired();
        e.Property(x => x.EffectCommandId).HasColumnName("effect_command_id");
        e.Property(x => x.EvidenceRef).HasColumnName("evidence_ref");
        e.Property(x => x.Output).HasColumnName("output").HasColumnType("jsonb");
        e.Property(x => x.StartedAt).HasColumnName("started_at").IsRequired();
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        e.ToTable(t => t.HasCheckConstraint("workflow_step_log_status_check",
            CheckSql.InList("status", ["pending", "running", "done", "failed", "skipped", "shadow"])));
        e.HasOne(x => x.Instance).WithMany().HasForeignKey(x => x.InstanceId)
            .HasConstraintName("workflow_step_log_instance_id_fkey");
    }
}
