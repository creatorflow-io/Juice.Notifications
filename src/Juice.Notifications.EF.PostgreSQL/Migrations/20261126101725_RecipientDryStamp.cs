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
                nullable: false,
                defaultValue: false);

            // Precondition: upgrade to this version out of dry run (Delivery:DryRun off, Notifications:Pipeline not shadow). An escalation step's
            // recipients already there are left real: the mode their step was planned in isn't recorded, so one planned dry and not yet delivered
            // would go out. No deployment ran dry before this (Observability's shadow needs a later alpha, #16 review).
            // The request's own recipients take its flag (M44-2b stamped it), so a dry request's recipients not yet planned stay dry.
            migrationBuilder.Sql("""
                UPDATE notifications.notification_recipients r SET dry_run = q.dry_run
                FROM notifications.notifications n JOIN notifications.requests q ON q.tenant_id = n.tenant_id AND q.id = n.request_id
                WHERE n.tenant_id = r.tenant_id AND n.id = r.notification_id AND r.escalation_step = 0 AND q.dry_run
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
