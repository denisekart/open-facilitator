using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddHeldActionsAndConsentColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.AddColumn<bool>(
                name: "not_requester",
                schema: "facilitator",
                table: "approvals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "nudged_at",
                schema: "facilitator",
                table: "approvals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_by",
                schema: "facilitator",
                table: "approvals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rule_id",
                schema: "facilitator",
                table: "approvals",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "held_actions",
                schema: "facilitator",
                columns: table => new
                {
                    held_action_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    capability_id = table.Column<string>(type: "text", nullable: false),
                    endpoint_id = table.Column<string>(type: "text", nullable: false),
                    envelope = table.Column<string>(type: "jsonb", nullable: false),
                    action_digest = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    nudge_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_held_actions", x => x.held_action_id);
                    table.CheckConstraint("held_actions_state_check", "state IN ('pending', 'approved', 'denied', 'expired', 'revoked', 'delivered')");
                    table.ForeignKey(
                        name: "held_actions_approval_id_fkey",
                        column: x => x.approval_id,
                        principalSchema: "facilitator",
                        principalTable: "approvals",
                        principalColumn: "approval_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered', 'provider.duplicate', 'work.linked', 'work.triaged', 'approval.nudged', 'work.consent-expired')");

            migrationBuilder.CreateIndex(
                name: "held_actions_approval_id_key",
                schema: "facilitator",
                table: "held_actions",
                column: "approval_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "held_actions_sweep_idx",
                schema: "facilitator",
                table: "held_actions",
                columns: new[] { "state", "nudge_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "held_actions",
                schema: "facilitator");

            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.DropColumn(
                name: "not_requester",
                schema: "facilitator",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "nudged_at",
                schema: "facilitator",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "requested_by",
                schema: "facilitator",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "rule_id",
                schema: "facilitator",
                table: "approvals");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered', 'provider.duplicate', 'work.linked', 'work.triaged')");
        }
    }
}
