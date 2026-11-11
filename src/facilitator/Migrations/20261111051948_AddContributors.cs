using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddContributors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.AddColumn<Guid>(
                name: "contributor_id",
                schema: "facilitator",
                table: "link_enrollments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "contributors",
                schema: "facilitator",
                columns: table => new
                {
                    contributor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    remit = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    changed_by = table.Column<string>(type: "text", nullable: true),
                    change_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contributors", x => x.contributor_id);
                    table.CheckConstraint("contributors_status_check", "status IN ('active', 'retired')");
                    table.ForeignKey(
                        name: "contributors_team_id_fkey",
                        column: x => x.team_id,
                        principalSchema: "facilitator",
                        principalTable: "teams",
                        principalColumn: "team_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered', 'provider.duplicate', 'work.linked', 'work.triaged', 'approval.nudged', 'work.consent-expired', 'address.obsolete', 'review.decision')");

            migrationBuilder.CreateIndex(
                name: "contributors_handle_key",
                schema: "facilitator",
                table: "contributors",
                column: "handle",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "contributors_team_id_idx",
                schema: "facilitator",
                table: "contributors",
                column: "team_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contributors",
                schema: "facilitator");

            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.DropColumn(
                name: "contributor_id",
                schema: "facilitator",
                table: "link_enrollments");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered', 'provider.duplicate', 'work.linked', 'work.triaged', 'approval.nudged', 'work.consent-expired', 'address.obsolete')");
        }
    }
}
