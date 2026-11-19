using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ReviewFixes5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Its own migration, so its own transaction: the column (ReviewFixes4) takes the table's ACCESS EXCLUSIVE lock only briefly, and this
            // build holds a SHARE lock (reads go on, writes wait) for one scan of notification_recipients. The index starts empty: the column is
            // false everywhere. Not CONCURRENTLY: that can't be rerun after a failure, releases the migration lock early, and can't run in an
            // idempotent script.
            migrationBuilder.CreateIndex(
                name: "ix_notification_recipients_failure_check",
                schema: "notifications",
                table: "notification_recipients",
                column: "id",
                filter: "failure_check_due AND next_at IS NULL AND outcome NOT IN ('Pending', 'Deferred')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notification_recipients_failure_check",
                schema: "notifications",
                table: "notification_recipients");
        }
    }
}
