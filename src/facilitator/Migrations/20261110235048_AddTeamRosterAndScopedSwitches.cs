using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamRosterAndScopedSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_members",
                schema: "facilitator",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    capacity = table.Column<string>(type: "text", nullable: false),
                    added_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_members", x => new { x.team_id, x.actor });
                    table.CheckConstraint("team_members_capacity_check", "capacity IN ('member', 'operator')");
                    table.CheckConstraint("team_members_kind_check", "kind IN ('human', 'contributor')");
                    table.ForeignKey(
                        name: "team_members_team_id_fkey",
                        column: x => x.team_id,
                        principalSchema: "facilitator",
                        principalTable: "teams",
                        principalColumn: "team_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_settings",
                schema: "facilitator",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_kind = table.Column<string>(type: "text", nullable: false),
                    scope_id = table.Column<string>(type: "text", nullable: false),
                    resource = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    author = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_settings", x => new { x.team_id, x.scope_kind, x.scope_id, x.resource });
                    table.CheckConstraint("team_settings_scope_kind_check", "scope_kind IN ('team', 'channel')");
                    table.ForeignKey(
                        name: "team_settings_team_id_fkey",
                        column: x => x.team_id,
                        principalSchema: "facilitator",
                        principalTable: "teams",
                        principalColumn: "team_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "team_members_actor_idx",
                schema: "facilitator",
                table: "team_members",
                column: "actor");

            migrationBuilder.CreateIndex(
                name: "team_settings_lookup_idx",
                schema: "facilitator",
                table: "team_settings",
                columns: new[] { "resource", "scope_kind", "scope_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_members",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "team_settings",
                schema: "facilitator");
        }
    }
}
