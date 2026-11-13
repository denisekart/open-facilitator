using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.AddColumn<Guid>(
                name: "workflow_instance_id",
                schema: "facilitator",
                table: "works",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workflow_definitions",
                schema: "facilitator",
                columns: table => new
                {
                    def_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    digest = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    shadow = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    team = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definitions", x => new { x.def_id, x.version });
                    table.CheckConstraint("workflow_definitions_source_check", "source IN ('gallery', 'hand', 'ai', 'derived')");
                });

            migrationBuilder.CreateTable(
                name: "workflow_instances",
                schema: "facilitator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    def_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    def_version = table.Column<int>(type: "integer", nullable: false),
                    team = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    project = table.Column<Guid>(type: "uuid", nullable: true),
                    primary_work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trigger_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    current_step = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    deadline_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    parent_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    invocation_path = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    output = table.Column<string>(type: "jsonb", nullable: true),
                    end_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_instances", x => x.id);
                    table.CheckConstraint("workflow_instances_end_reason_check", "end_reason IN ('budget-exhausted', 'chain-age-exceeded', 'parent-cancelled', 'invoke-timeout', 'operator-cancel') OR end_reason IS NULL");
                    table.CheckConstraint("workflow_instances_status_check", "status IN ('running', 'waiting', 'completed', 'failed', 'cancelled', 'shadowed')");
                    table.ForeignKey(
                        name: "workflow_instances_parent_instance_id_fkey",
                        column: x => x.parent_instance_id,
                        principalSchema: "facilitator",
                        principalTable: "workflow_instances",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "workflow_step_log",
                schema: "facilitator",
                columns: table => new
                {
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    step_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    effect_command_id = table.Column<string>(type: "text", nullable: true),
                    evidence_ref = table.Column<string>(type: "text", nullable: true),
                    output = table.Column<string>(type: "jsonb", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_step_log", x => new { x.instance_id, x.seq });
                    table.CheckConstraint("workflow_step_log_status_check", "status IN ('pending', 'running', 'done', 'failed', 'skipped', 'shadow')");
                    table.ForeignKey(
                        name: "workflow_step_log_instance_id_fkey",
                        column: x => x.instance_id,
                        principalSchema: "facilitator",
                        principalTable: "workflow_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "works_workflow_instance_id_idx",
                schema: "facilitator",
                table: "works",
                column: "workflow_instance_id");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered', 'provider.duplicate', 'work.linked', 'work.triaged', 'approval.nudged', 'work.consent-expired', 'address.obsolete', 'review.decision', 'effects-disclosed', 'assignment.queue-reason', 'workflow.stage')");

            migrationBuilder.CreateIndex(
                name: "workflow_instances_def_id_idx",
                schema: "facilitator",
                table: "workflow_instances",
                column: "def_id");

            migrationBuilder.CreateIndex(
                name: "workflow_instances_parent_instance_id_idx",
                schema: "facilitator",
                table: "workflow_instances",
                column: "parent_instance_id");

            migrationBuilder.CreateIndex(
                name: "workflow_instances_primary_work_id_idx",
                schema: "facilitator",
                table: "workflow_instances",
                column: "primary_work_id");

            migrationBuilder.CreateIndex(
                name: "workflow_instances_status_deadline_at_idx",
                schema: "facilitator",
                table: "workflow_instances",
                columns: new[] { "status", "deadline_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_definitions",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "workflow_step_log",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "workflow_instances",
                schema: "facilitator");

            migrationBuilder.DropIndex(
                name: "works_workflow_instance_id_idx",
                schema: "facilitator",
                table: "works");

            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.DropColumn(
                name: "workflow_instance_id",
                schema: "facilitator",
                table: "works");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered', 'provider.duplicate', 'work.linked', 'work.triaged', 'approval.nudged', 'work.consent-expired', 'address.obsolete', 'review.decision', 'effects-disclosed', 'assignment.queue-reason')");
        }
    }
}
