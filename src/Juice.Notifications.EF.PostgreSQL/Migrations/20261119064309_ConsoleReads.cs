using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ConsoleReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The overview's unfinished count (phase-4c-plan I52). Not CONCURRENTLY, as ReviewFixes5: a SHARE lock (reads go on, writes wait)
            // for one scan of notifications.
            migrationBuilder.CreateIndex(
                name: "ix_notifications_unfinished",
                schema: "notifications",
                table: "notifications",
                column: "tenant_id",
                filter: "state IN ('Planning', 'Running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifications_unfinished",
                schema: "notifications",
                table: "notifications");
        }
    }
}
