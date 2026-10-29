using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Facilitator.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_config",
                schema: "facilitator",
                columns: table => new
                {
                    resource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    author = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_config", x => x.resource);
                });

            migrationBuilder.CreateTable(
                name: "admin_config_history",
                schema: "facilitator",
                columns: table => new
                {
                    resource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    author = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_config_history", x => new { x.resource, x.version });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_config",
                schema: "facilitator");

            migrationBuilder.DropTable(
                name: "admin_config_history",
                schema: "facilitator");
        }
    }
}
