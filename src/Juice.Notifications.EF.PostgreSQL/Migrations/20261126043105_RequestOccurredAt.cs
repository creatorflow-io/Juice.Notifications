using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RequestOccurredAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // When a request's event happened (phase 5, M45-n): an escalation's first step is due from it. Nullable: requests from before it was
            // kept read their acceptance time; no backfill, no rewrite.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "occurred_at",
                schema: "notifications",
                table: "requests",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "occurred_at",
                schema: "notifications",
                table: "requests");
        }
    }
}
