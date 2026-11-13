using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <summary>
    /// Juice.Messaging.Outbox.EF 10.13.0 quotes the column names in its outbox index filters. The same indexes, rebuilt with the new text
    /// so the model and the database agree.
    /// </summary>
    public partial class OutboxIndexFilterQuoting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxDeliveries_Pending",
                schema: "notifications",
                table: "outbox_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_OutboxDeliveries_Recovery",
                schema: "notifications",
                table: "outbox_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_OutboxDeliveries_Retry",
                schema: "notifications",
                table: "outbox_deliveries");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Pending",
                schema: "notifications",
                table: "outbox_deliveries",
                column: "creation_time",
                filter: "\"state\" = 0")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Recovery",
                schema: "notifications",
                table: "outbox_deliveries",
                column: "processed_on",
                filter: "\"state\" = 1")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Retry",
                schema: "notifications",
                table: "outbox_deliveries",
                column: "next_attempt_on",
                filter: "\"state\" = 3 AND \"next_attempt_on\" IS NOT NULL")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxDeliveries_Pending",
                schema: "notifications",
                table: "outbox_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_OutboxDeliveries_Recovery",
                schema: "notifications",
                table: "outbox_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_OutboxDeliveries_Retry",
                schema: "notifications",
                table: "outbox_deliveries");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Pending",
                schema: "notifications",
                table: "outbox_deliveries",
                column: "creation_time",
                filter: "state = 0")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Recovery",
                schema: "notifications",
                table: "outbox_deliveries",
                column: "processed_on",
                filter: "state = 1")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Retry",
                schema: "notifications",
                table: "outbox_deliveries",
                column: "next_attempt_on",
                filter: "state = 3 AND next_attempt_on IS NOT NULL")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });
        }
    }
}
