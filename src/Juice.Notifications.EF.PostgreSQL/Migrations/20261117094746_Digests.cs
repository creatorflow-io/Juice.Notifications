using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class Digests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "digest_id",
                schema: "notifications",
                table: "notification_recipients",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "digest_id",
                schema: "notifications",
                table: "deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "digests",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opens_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closes_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    members = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    claimed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    step_failures = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_digests", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_recipients_digest",
                schema: "notifications",
                table: "notification_recipients",
                columns: new[] { "tenant_id", "digest_id" },
                filter: "digest_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_digest",
                schema: "notifications",
                table: "deliveries",
                columns: new[] { "tenant_id", "digest_id" },
                filter: "digest_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_digests_due",
                schema: "notifications",
                table: "digests",
                column: "next_at",
                filter: "next_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_digests_open",
                schema: "notifications",
                table: "digests",
                columns: new[] { "tenant_id", "type_key", "by", "key" },
                unique: true,
                filter: "state = 'Open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "digests",
                schema: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_notification_recipients_digest",
                schema: "notifications",
                table: "notification_recipients");

            migrationBuilder.DropIndex(
                name: "ix_deliveries_digest",
                schema: "notifications",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "digest_id",
                schema: "notifications",
                table: "notification_recipients");

            migrationBuilder.DropColumn(
                name: "digest_id",
                schema: "notifications",
                table: "deliveries");
        }
    }
}
