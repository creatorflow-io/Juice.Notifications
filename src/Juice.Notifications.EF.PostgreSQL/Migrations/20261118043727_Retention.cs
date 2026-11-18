using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class Retention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "content_retention_days",
                schema: "notifications",
                table: "tenant_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "request_retention_days",
                schema: "notifications",
                table: "tenant_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "redacted_at",
                schema: "notifications",
                table: "requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_requests_unredacted",
                schema: "notifications",
                table: "requests",
                column: "accepted_at",
                filter: "redacted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_requests_unredacted",
                schema: "notifications",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "content_retention_days",
                schema: "notifications",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "request_retention_days",
                schema: "notifications",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "redacted_at",
                schema: "notifications",
                table: "requests");
        }
    }
}
