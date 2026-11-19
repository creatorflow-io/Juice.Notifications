using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ReviewFixes4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "failure_check_due",
                schema: "notifications",
                table: "notification_recipients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Built concurrently, outside the migration's transaction: notification_recipients is written by every stage, and a plain CREATE INDEX
            // would hold its writes for the whole build. The model (snapshot) has the same index, so later migrations see it as there. If the
            // build fails it leaves an invalid index: drop it and run the migration again (IF NOT EXISTS would keep the invalid one).
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY ix_notification_recipients_failure_check ON notifications.notification_recipients (id)
                WHERE failure_check_due AND next_at IS NULL AND outcome NOT IN ('Pending', 'Deferred')
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS notifications.ix_notification_recipients_failure_check", suppressTransaction: true);

            migrationBuilder.DropColumn(
                name: "failure_check_due",
                schema: "notifications",
                table: "notification_recipients");
        }
    }
}
