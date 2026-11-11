using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RecipientNamed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notification_recipients_tenant_id_notification_id",
                schema: "notifications",
                table: "notification_recipients");

            migrationBuilder.CreateIndex(
                name: "ix_notification_recipients_named",
                schema: "notifications",
                table: "notification_recipients",
                columns: new[] { "tenant_id", "notification_id", "escalation_step", "named_as" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notification_recipients_named",
                schema: "notifications",
                table: "notification_recipients");

            migrationBuilder.CreateIndex(
                name: "ix_notification_recipients_tenant_id_notification_id",
                schema: "notifications",
                table: "notification_recipients",
                columns: new[] { "tenant_id", "notification_id" });
        }
    }
}
