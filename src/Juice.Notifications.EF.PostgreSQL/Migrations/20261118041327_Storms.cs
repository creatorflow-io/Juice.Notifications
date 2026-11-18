using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class Storms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "route_minutes",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route_key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    minute = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_route_minutes", x => new { x.tenant_id, x.route_key, x.minute });
                });

            migrationBuilder.CreateTable(
                name: "storms",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    route_key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_high_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    peak = table.Column<int>(type: "integer", nullable: false),
                    digested = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_storms", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "tenant_settings",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storm_threshold = table.Column<int>(type: "integer", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_settings", x => x.tenant_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_route_minutes_minute",
                schema: "notifications",
                table: "route_minutes",
                column: "minute");

            migrationBuilder.CreateIndex(
                name: "ix_storms_open",
                schema: "notifications",
                table: "storms",
                columns: new[] { "tenant_id", "route_key" },
                unique: true,
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_storms_tenant_id_started_at",
                schema: "notifications",
                table: "storms",
                columns: new[] { "tenant_id", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "route_minutes",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "storms",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "tenant_settings",
                schema: "notifications");
        }
    }
}
