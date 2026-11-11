using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "facilitator",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    audience = table.Column<string>(type: "text", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor = table.Column<string>(type: "text", nullable: true),
                    level = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "jsonb", nullable: false),
                    fallback = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    correction_window_ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.notification_id);
                    table.CheckConstraint("notifications_audience_check", "audience IN ('actor', 'team', 'operators')");
                    table.CheckConstraint("notifications_level_check", "level IN ('info', 'notice', 'warning', 'critical')");
                    table.CheckConstraint("notifications_state_check", "state IN ('pending', 'sent', 'fell-back', 'undeliverable', 'obsolete')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered', 'provider.duplicate', 'work.linked', 'work.triaged', 'approval.nudged', 'work.consent-expired', 'address.obsolete')");

            migrationBuilder.CreateIndex(
                name: "notifications_state_idx",
                schema: "facilitator",
                table: "notifications",
                columns: new[] { "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "notifications_work_id_idx",
                schema: "facilitator",
                table: "notifications",
                column: "work_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications",
                schema: "facilitator");

            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered', 'provider.duplicate', 'work.linked', 'work.triaged', 'approval.nudged', 'work.consent-expired')");
        }
    }
}
