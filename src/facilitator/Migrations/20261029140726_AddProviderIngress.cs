using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderIngress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events");

            migrationBuilder.CreateTable(
                name: "provider_identities",
                schema: "facilitator",
                columns: table => new
                {
                    identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    tenant = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<string>(type: "text", nullable: false),
                    oidc_issuer = table.Column<string>(type: "text", nullable: false),
                    oidc_subject = table.Column<string>(type: "text", nullable: false),
                    challenge_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_identities", x => x.identity_id);
                });

            migrationBuilder.CreateTable(
                name: "provider_link_challenges",
                schema: "facilitator",
                columns: table => new
                {
                    challenge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_sha256 = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    tenant = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<string>(type: "text", nullable: false),
                    deliver_to_account_id = table.Column<string>(type: "text", nullable: false),
                    oidc_issuer = table.Column<string>(type: "text", nullable: true),
                    oidc_subject = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    redeemed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_link_challenges", x => x.challenge_id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted', 'provider.input', 'provider.reaction')");

            migrationBuilder.CreateIndex(
                name: "communication_bindings_conversation_unique",
                schema: "facilitator",
                table: "communication_bindings",
                columns: new[] { "provider", "channel", "root_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "communication_bindings_idempotency_unique",
                schema: "facilitator",
                table: "communication_bindings",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "provider_identities_account_unique",
                schema: "facilitator",
                table: "provider_identities",
                columns: new[] { "provider", "tenant", "account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "provider_link_challenges_account_idx",
                schema: "facilitator",
                table: "provider_link_challenges",
                columns: new[] { "provider", "tenant", "account_id" });

            migrationBuilder.CreateIndex(
                name: "provider_link_challenges_code_idx",
                schema: "facilitator",
                table: "provider_link_challenges",
                column: "code_sha256");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_identities",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "provider_link_challenges",
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
                name: "communication_bindings_idempotency_unique",
                schema: "facilitator",
                table: "communication_bindings");

            migrationBuilder.AddCheckConstraint(
                name: "work_events_kind_check",
                schema: "facilitator",
                table: "work_events",
                sql: "kind IN ('created', 'state.transition', 'steer', 'command.accepted', 'assignment.transition', 'approval.decided', 'artifact.stored', 'artifact.registered', 'artifact.available', 'artifact.gc', 'manifest.recorded', 'manifest.superseded', 'memory.candidate', 'memory.promoted')");
        }
    }
}
