using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddDagNudges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "depends_on",
                schema: "facilitator",
                table: "assignments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "nudges",
                schema: "facilitator",
                columns: table => new
                {
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    stall_episode = table.Column<int>(type: "integer", nullable: false),
                    nudge_count = table.Column<int>(type: "integer", nullable: false),
                    last_nudge_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_progress_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nudges", x => new { x.assignment_id, x.generation, x.stall_episode });
                    table.ForeignKey(
                        name: "nudges_assignment_id_fkey",
                        column: x => x.assignment_id,
                        principalSchema: "facilitator",
                        principalTable: "assignments",
                        principalColumn: "assignment_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nudges",
                schema: "facilitator");

            migrationBuilder.DropColumn(
                name: "depends_on",
                schema: "facilitator",
                table: "assignments");
        }
    }
}
