using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RecipientDryStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "dry_run",
                schema: "notifications",
                table: "notification_recipients",
                type: "boolean",
                nullable: true);

            // Recipients already there. The request's own take its flag (M44-2b stamped it), so a dry request's recipients not yet planned stay
            // dry. An escalation step's are left null: the mode their step was planned in wasn't recorded, so they take the mode when they are
            // planned, as M44-2b did (DeliveryPlannerStage, JoinDigestAsync). A host upgraded while in shadow plans them dry, so they never page
            // beside legacy (#16 review, QA). Every recipient created from here on has a value.
            migrationBuilder.Sql("""
                UPDATE notifications.notification_recipients r SET dry_run = q.dry_run
                FROM notifications.notifications n JOIN notifications.requests q ON q.tenant_id = n.tenant_id AND q.id = n.request_id
                WHERE n.tenant_id = r.tenant_id AND n.id = r.notification_id AND r.escalation_step = 0
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "dry_run",
                schema: "notifications",
                table: "notification_recipients");
        }
    }
}
