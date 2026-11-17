using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ProviderCallbacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_events",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    provider_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_events", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_provider_message",
                schema: "notifications",
                table: "deliveries",
                columns: new[] { "tenant_id", "provider_message_id" },
                filter: "provider_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_provider_events_delivery",
                schema: "notifications",
                table: "provider_events",
                columns: new[] { "tenant_id", "delivery_id" },
                filter: "delivery_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_provider_events_once",
                schema: "notifications",
                table: "provider_events",
                columns: new[] { "tenant_id", "provider", "provider_event_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_events",
                schema: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_deliveries_provider_message",
                schema: "notifications",
                table: "deliveries");
        }
    }
}
