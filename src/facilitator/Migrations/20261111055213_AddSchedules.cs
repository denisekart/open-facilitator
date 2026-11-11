using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "schedules",
                schema: "facilitator",
                columns: table => new
                {
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    objective = table.Column<string>(type: "text", nullable: false),
                    cadence = table.Column<string>(type: "text", nullable: false),
                    timezone = table.Column<string>(type: "text", nullable: true),
                    next_run_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_run_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    owner_actor = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedules", x => x.schedule_id);
                    table.ForeignKey(
                        name: "schedules_team_id_fkey",
                        column: x => x.team_id,
                        principalSchema: "facilitator",
                        principalTable: "teams",
                        principalColumn: "team_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_schedules_team_id",
                schema: "facilitator",
                table: "schedules",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "schedules_due_idx",
                schema: "facilitator",
                table: "schedules",
                columns: new[] { "enabled", "next_run_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedules",
                schema: "facilitator");
        }
    }
}
