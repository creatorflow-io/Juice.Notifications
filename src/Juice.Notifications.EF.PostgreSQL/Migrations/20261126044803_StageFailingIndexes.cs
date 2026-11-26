using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class StageFailingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_notifications_failing",
                schema: "notifications",
                table: "notifications",
                column: "step_failures",
                filter: "step_failures > 0 AND next_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notification_recipients_failing",
                schema: "notifications",
                table: "notification_recipients",
                column: "step_failures",
                filter: "step_failures > 0 AND next_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_escalations_failing",
                schema: "notifications",
                table: "escalations",
                column: "step_failures",
                filter: "step_failures > 0 AND next_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_digests_failing",
                schema: "notifications",
                table: "digests",
                column: "step_failures",
                filter: "step_failures > 0 AND next_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_failing",
                schema: "notifications",
                table: "deliveries",
                column: "step_failures",
                filter: "step_failures > 0 AND next_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifications_failing",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_notification_recipients_failing",
                schema: "notifications",
                table: "notification_recipients");

            migrationBuilder.DropIndex(
                name: "ix_escalations_failing",
                schema: "notifications",
                table: "escalations");

            migrationBuilder.DropIndex(
                name: "ix_digests_failing",
                schema: "notifications",
                table: "digests");

            migrationBuilder.DropIndex(
                name: "ix_deliveries_failing",
                schema: "notifications",
                table: "deliveries");
        }
    }
}
