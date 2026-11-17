using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddRouterTeamSignOver : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.DropIndex(
                name: "communication_bindings_conversation_unique",
                schema: "facilitator",
                table: "communication_bindings");

            migrationBuilder.AddColumn<string>(
                name: "team",
                schema: "facilitator",
                table: "communication_bindings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "artifact_grants",
                schema: "facilitator",
                columns: table => new
                {
                    grant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grantee_team_slug = table.Column<string>(type: "text", nullable: false),
                    grantee_endpoint_id = table.Column<string>(type: "text", nullable: false),
                    command_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    minted_by = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consume_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_grants", x => x.grant_id);
                    table.CheckConstraint("artifact_grants_consume_reason_check", "consume_reason IN ('read', 'receipt', 'expiry')");
                    table.CheckConstraint("artifact_grants_status_check", "status IN ('active', 'consumed', 'revoked', 'expired')");
                    table.ForeignKey(
                        name: "artifact_grants_artifact_id_fkey",
                        column: x => x.artifact_id,
                        principalSchema: "facilitator",
                        principalTable: "artifacts",
                        principalColumn: "artifact_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered', 'provider.duplicate', 'work.linked', 'work.triaged', 'approval.nudged', 'work.consent-expired', 'address.obsolete', 'review.decision', 'effects-disclosed', 'assignment.queue-reason', 'artifact.signed-over', 'workflow.stage')");

            migrationBuilder.CreateIndex(
                name: "communication_bindings_conversation_unique",
                schema: "facilitator",
                table: "communication_bindings",
                columns: new[] { "provider", "channel", "root_message_id" },
                unique: true,
                filter: "work_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "communication_bindings_conversation_work_unique",
                schema: "facilitator",
                table: "communication_bindings",
                columns: new[] { "provider", "channel", "root_message_id", "work_id" },
                unique: true,
                filter: "work_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "artifact_grants_artifact_command_unique",
                schema: "facilitator",
                table: "artifact_grants",
                columns: new[] { "artifact_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "artifact_grants_open_idx",
                schema: "facilitator",
                table: "artifact_grants",
                columns: new[] { "artifact_id", "grantee_endpoint_id" },
                filter: "status = 'active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifact_grants",
                schema: "facilitator");

            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.DropIndex(
                name: "communication_bindings_conversation_unique",
                schema: "facilitator",
                table: "communication_bindings");

            migrationBuilder.DropIndex(
                name: "communication_bindings_conversation_work_unique",
                schema: "facilitator",
                table: "communication_bindings");

            migrationBuilder.DropColumn(
                name: "team",
                schema: "facilitator",
                table: "communication_bindings");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered', 'provider.duplicate', 'work.linked', 'work.triaged', 'approval.nudged', 'work.consent-expired', 'address.obsolete', 'review.decision', 'effects-disclosed', 'assignment.queue-reason', 'workflow.stage')");

            migrationBuilder.CreateIndex(
                name: "communication_bindings_conversation_unique",
                schema: "facilitator",
                table: "communication_bindings",
                columns: new[] { "provider", "channel", "root_message_id" },
                unique: true);
        }
    }
}
