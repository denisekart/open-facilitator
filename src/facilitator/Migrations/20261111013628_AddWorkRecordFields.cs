using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkRecordFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.AddColumn<string>(
                name: "active_where",
                schema: "facilitator",
                table: "works",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "close_reason",
                schema: "facilitator",
                table: "works",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost",
                schema: "facilitator",
                table: "works",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_into",
                schema: "facilitator",
                table: "works",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "origin",
                schema: "facilitator",
                table: "works",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "owner_actor",
                schema: "facilitator",
                table: "works",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "team_id",
                schema: "facilitator",
                table: "works",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "routing_triage",
                schema: "facilitator",
                table: "routing_decisions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "work_links",
                schema: "facilitator",
                columns: table => new
                {
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ref_work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_links", x => x.link_id);
                    table.CheckConstraint("work_links_kind_check", "kind IN ('successor', 'parent', 'child', 'merged-duplicate', 'context')");
                    table.ForeignKey(
                        name: "work_links_ref_work_id_fkey",
                        column: x => x.ref_work_id,
                        principalSchema: "facilitator",
                        principalTable: "works",
                        principalColumn: "work_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "work_links_work_id_fkey",
                        column: x => x.work_id,
                        principalSchema: "facilitator",
                        principalTable: "works",
                        principalColumn: "work_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "works_merged_into_idx",
                schema: "facilitator",
                table: "works",
                column: "merged_into");

            migrationBuilder.CreateIndex(
                name: "works_team_id_idx",
                schema: "facilitator",
                table: "works",
                column: "team_id");

            // Backfill: a work's team is its project's team when the work names no team itself.
            migrationBuilder.Sql(
                "UPDATE facilitator.works w SET team_id = p.team_id FROM facilitator.projects p WHERE w.project_id = p.project_id AND w.team_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered', 'provider.duplicate', 'work.linked', 'work.triaged')");

            migrationBuilder.CreateIndex(
                name: "work_links_ref_idx",
                schema: "facilitator",
                table: "work_links",
                column: "ref_work_id");

            migrationBuilder.CreateIndex(
                name: "work_links_unique",
                schema: "facilitator",
                table: "work_links",
                columns: new[] { "work_id", "ref_work_id", "kind" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "works_team_id_fkey",
                schema: "facilitator",
                table: "works",
                column: "team_id",
                principalSchema: "facilitator",
                principalTable: "teams",
                principalColumn: "team_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "works_team_id_fkey",
                schema: "facilitator",
                table: "works");

            migrationBuilder.DropTable(
                name: "work_links",
                schema: "facilitator");

            migrationBuilder.DropIndex(
                name: "works_merged_into_idx",
                schema: "facilitator",
                table: "works");

            migrationBuilder.DropIndex(
                name: "works_team_id_idx",
                schema: "facilitator",
                table: "works");

            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.DropColumn(
                name: "active_where",
                schema: "facilitator",
                table: "works");

            migrationBuilder.DropColumn(
                name: "close_reason",
                schema: "facilitator",
                table: "works");

            migrationBuilder.DropColumn(
                name: "cost",
                schema: "facilitator",
                table: "works");

            migrationBuilder.DropColumn(
                name: "merged_into",
                schema: "facilitator",
                table: "works");

            migrationBuilder.DropColumn(
                name: "origin",
                schema: "facilitator",
                table: "works");

            migrationBuilder.DropColumn(
                name: "owner_actor",
                schema: "facilitator",
                table: "works");

            migrationBuilder.DropColumn(
                name: "team_id",
                schema: "facilitator",
                table: "works");

            migrationBuilder.DropColumn(
                name: "routing_triage",
                schema: "facilitator",
                table: "routing_decisions");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.attachment', 'provider.reaction', 'input.required', 'input.answered')");
        }
    }
}
