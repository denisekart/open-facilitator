using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddRoutingDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "opened_at",
                schema: "facilitator",
                table: "link_sessions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Existing sessions predate the column; their last heartbeat is
            // the best available "first opened" so the oldest-online order
            // does not privilege a re-opened row over a long-lived one.
            migrationBuilder.Sql(
                "UPDATE facilitator.link_sessions SET opened_at = last_heartbeat");

            migrationBuilder.CreateTable(
                name: "routing_decisions",
                schema: "facilitator",
                columns: table => new
                {
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_command_id = table.Column<string>(type: "text", nullable: false),
                    capability_id = table.Column<string>(type: "text", nullable: false),
                    team_slug = table.Column<string>(type: "text", nullable: true),
                    allowed_candidates = table.Column<string>(type: "jsonb", nullable: false),
                    chosen_endpoint_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    basis = table.Column<string>(type: "text", nullable: false),
                    provider_ref = table.Column<string>(type: "text", nullable: true),
                    last_routing_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routing_decisions", x => x.decision_id);
                    table.CheckConstraint("routing_decisions_status_check", "status IN ('pending', 'routed', 'abandoned')");
                    table.ForeignKey(
                        name: "routing_decisions_work_id_fkey",
                        column: x => x.work_id,
                        principalSchema: "facilitator",
                        principalTable: "works",
                        principalColumn: "work_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_routing_decisions_work_id",
                schema: "facilitator",
                table: "routing_decisions",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "routing_decisions_intent_unique",
                schema: "facilitator",
                table: "routing_decisions",
                column: "intent_command_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "routing_decisions_pending",
                schema: "facilitator",
                table: "routing_decisions",
                columns: new[] { "status", "last_routing_attempt_at" },
                filter: "status = 'pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "routing_decisions",
                schema: "facilitator");

            migrationBuilder.DropColumn(
                name: "opened_at",
                schema: "facilitator",
                table: "link_sessions");
        }
    }
}
