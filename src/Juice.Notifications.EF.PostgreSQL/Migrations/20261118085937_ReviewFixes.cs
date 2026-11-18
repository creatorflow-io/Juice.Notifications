using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_requests_unredacted",
                schema: "notifications",
                table: "requests");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "failure_raised_at",
                schema: "notifications",
                table: "notification_recipients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_requests_unredacted",
                schema: "notifications",
                table: "requests",
                columns: new[] { "type_id", "accepted_at" },
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
                name: "failure_raised_at",
                schema: "notifications",
                table: "notification_recipients");

            migrationBuilder.CreateIndex(
                name: "ix_requests_unredacted",
                schema: "notifications",
                table: "requests",
                column: "accepted_at",
                filter: "redacted_at IS NULL");
        }
    }
}
