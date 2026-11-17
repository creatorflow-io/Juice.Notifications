using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class Correlations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "correlation_id",
                schema: "notifications",
                table: "requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "correlation_id",
                schema: "notifications",
                table: "inbox_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "correlations",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    acknowledged_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    resolved_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reopened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_request_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_correlations", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_correlations_application_tenants_application_id_tenant_id",
                        columns: x => new { x.application_id, x.tenant_id },
                        principalSchema: "notifications",
                        principalTable: "application_tenants",
                        principalColumns: new[] { "application_id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_requests_tenant_id_correlation_id",
                schema: "notifications",
                table: "requests",
                columns: new[] { "tenant_id", "correlation_id" },
                filter: "correlation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_items_correlation",
                schema: "notifications",
                table: "inbox_items",
                columns: new[] { "tenant_id", "correlation_id" },
                filter: "correlation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_correlations_application_id_tenant_id",
                schema: "notifications",
                table: "correlations",
                columns: new[] { "application_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_correlations_key",
                schema: "notifications",
                table: "correlations",
                columns: new[] { "tenant_id", "application_id", "key" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_inbox_items_correlations_tenant_id_correlation_id",
                schema: "notifications",
                table: "inbox_items",
                columns: new[] { "tenant_id", "correlation_id" },
                principalSchema: "notifications",
                principalTable: "correlations",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_requests_correlations_tenant_id_correlation_id",
                schema: "notifications",
                table: "requests",
                columns: new[] { "tenant_id", "correlation_id" },
                principalSchema: "notifications",
                principalTable: "correlations",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_inbox_items_correlations_tenant_id_correlation_id",
                schema: "notifications",
                table: "inbox_items");

            migrationBuilder.DropForeignKey(
                name: "fk_requests_correlations_tenant_id_correlation_id",
                schema: "notifications",
                table: "requests");

            migrationBuilder.DropTable(
                name: "correlations",
                schema: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_requests_tenant_id_correlation_id",
                schema: "notifications",
                table: "requests");

            migrationBuilder.DropIndex(
                name: "ix_inbox_items_correlation",
                schema: "notifications",
                table: "inbox_items");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "notifications",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "notifications",
                table: "inbox_items");
        }
    }
}
