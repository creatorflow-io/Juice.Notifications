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

            migrationBuilder.DropColumn(
                name: "failure_check_due",
                schema: "notifications",
                table: "notification_recipients");
        }
    }
}
