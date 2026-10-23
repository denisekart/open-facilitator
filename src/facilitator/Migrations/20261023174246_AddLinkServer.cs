using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "link_deliveries",
                schema: "facilitator",
                columns: table => new
                {
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<string>(type: "text", nullable: false),
                    session_epoch = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    receipt = table.Column<string>(type: "jsonb", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    redeliver_after = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link_deliveries", x => x.delivery_id);
                    table.CheckConstraint("link_deliveries_state_check", "state IN ('pending', 'in_flight', 'acked')");
                    table.ForeignKey(
                        name: "link_deliveries_delivery_id_fkey",
                        column: x => x.delivery_id,
                        principalSchema: "facilitator",
                        principalTable: "outbox",
                        principalColumn: "outbox_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "link_enrollments",
                schema: "facilitator",
                columns: table => new
                {
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<string>(type: "text", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_sha256 = table.Column<string>(type: "text", nullable: false),
                    trust_mode = table.Column<string>(type: "text", nullable: false),
                    capability_manifest = table.Column<string>(type: "jsonb", nullable: true),
                    capabilities_digest = table.Column<string>(type: "text", nullable: true),
                    enrolled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link_enrollments", x => x.enrollment_id);
                    table.CheckConstraint("link_enrollments_trust_mode_check", "trust_mode IN ('shared-runtime', 'separate-process-same-host', 'isolated-executor')");
                    table.ForeignKey(
                        name: "link_enrollments_team_id_fkey",
                        column: x => x.team_id,
                        principalSchema: "facilitator",
                        principalTable: "teams",
                        principalColumn: "team_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "link_deliveries_endpoint_id_state_idx",
                schema: "facilitator",
                table: "link_deliveries",
                columns: new[] { "endpoint_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_link_enrollments_team_id",
                schema: "facilitator",
                table: "link_enrollments",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "link_enrollments_endpoint_id_key",
                schema: "facilitator",
                table: "link_enrollments",
                column: "endpoint_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "link_deliveries",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "link_enrollments",
                schema: "facilitator");
        }
    }
}
