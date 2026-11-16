using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkCredentialLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "token_sha256",
                schema: "facilitator",
                table: "link_enrollments",
                newName: "credential_class");

            // The renamed column carried a sha256 hex per row; the class domain
            // is {oidc, key} and every pre-lifecycle enrollment was mode-(a)
            // with a BEARER, not a key. The honest cutover: classify them 'key'
            // and revoke them — the old bearer died with this migration, so
            // each endpoint answers the first-boot law again (401 "no live
            // enrollment" → enroll + pin, which un-revokes the row). A row
            // left 'key' with no key row would challenge but never validate.
            migrationBuilder.Sql(
                "UPDATE facilitator.link_enrollments SET credential_class = 'key', revoked_at = now() "
                + "WHERE credential_class IS NULL OR credential_class NOT IN ('oidc', 'key')");

            migrationBuilder.AddColumn<long>(
                name: "epoch",
                schema: "facilitator",
                table: "link_enrollments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "seq",
                schema: "facilitator",
                table: "link_enrollments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "link_challenges",
                schema: "facilitator",
                columns: table => new
                {
                    nonce = table.Column<string>(type: "text", nullable: false),
                    endpoint_id = table.Column<string>(type: "text", nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link_challenges", x => x.nonce);
                });

            migrationBuilder.CreateTable(
                name: "link_endpoint_keys",
                schema: "facilitator",
                columns: table => new
                {
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "text", nullable: false),
                    public_key = table.Column<string>(type: "text", nullable: false),
                    key_algorithm = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    registered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link_endpoint_keys", x => new { x.enrollment_id, x.key_id });
                    table.CheckConstraint("link_endpoint_keys_key_algorithm_check", "key_algorithm IN ('ed25519')");
                    table.CheckConstraint("link_endpoint_keys_key_id_check", "key_id ~ '^sha256:[0-9a-f]{64}$'");
                    table.CheckConstraint("link_endpoint_keys_state_check", "state IN ('active', 'retired')");
                    table.ForeignKey(
                        name: "link_endpoint_keys_enrollment_id_fkey",
                        column: x => x.enrollment_id,
                        principalSchema: "facilitator",
                        principalTable: "link_enrollments",
                        principalColumn: "enrollment_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "link_enrollments_credential_class_check",
                schema: "facilitator",
                table: "link_enrollments",
                sql: "credential_class IN ('oidc', 'key')");

            migrationBuilder.CreateIndex(
                name: "link_challenges_expires_idx",
                schema: "facilitator",
                table: "link_challenges",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "link_endpoint_keys_key_id_idx",
                schema: "facilitator",
                table: "link_endpoint_keys",
                column: "key_id");

            migrationBuilder.CreateIndex(
                name: "link_endpoint_keys_one_active_per_enrollment",
                schema: "facilitator",
                table: "link_endpoint_keys",
                column: "enrollment_id",
                unique: true,
                filter: "state = 'active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "link_challenges",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "link_endpoint_keys",
                schema: "facilitator");

            migrationBuilder.DropCheckConstraint(
                name: "link_enrollments_credential_class_check",
                schema: "facilitator",
                table: "link_enrollments");

            migrationBuilder.DropColumn(
                name: "epoch",
                schema: "facilitator",
                table: "link_enrollments");

            migrationBuilder.DropColumn(
                name: "seq",
                schema: "facilitator",
                table: "link_enrollments");

            migrationBuilder.RenameColumn(
                name: "credential_class",
                schema: "facilitator",
                table: "link_enrollments",
                newName: "token_sha256");
        }
    }
}
