using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ClaimFailuresAndAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_delivery_attempts_tenant_id_delivery_id_number",
                schema: "notifications",
                table: "delivery_attempts");

            migrationBuilder.AddColumn<int>(
                name: "step_failures",
                schema: "notifications",
                table: "notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "step_failures",
                schema: "notifications",
                table: "notification_recipients",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "provider_message_id",
                schema: "notifications",
                table: "delivery_attempts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "step_failures",
                schema: "notifications",
                table: "deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Attempts recorded twice under one number (a worker whose lease ended and the new owner both took Attempts + 1) are renumbered
            // in the order they happened, so the unique index can be built.
            migrationBuilder.Sql("""
                UPDATE notifications.delivery_attempts AS a SET number = n.number
                FROM (SELECT tenant_id, id, row_number() OVER (PARTITION BY tenant_id, delivery_id ORDER BY at, number, id)::int AS number
                      FROM notifications.delivery_attempts) AS n
                WHERE a.tenant_id = n.tenant_id AND a.id = n.id AND a.number <> n.number
                """);

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_number",
                schema: "notifications",
                table: "delivery_attempts",
                columns: new[] { "tenant_id", "delivery_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_delivery_attempts_number",
                schema: "notifications",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "step_failures",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "step_failures",
                schema: "notifications",
                table: "notification_recipients");

            migrationBuilder.DropColumn(
                name: "provider_message_id",
                schema: "notifications",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "step_failures",
                schema: "notifications",
                table: "deliveries");

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_tenant_id_delivery_id_number",
                schema: "notifications",
                table: "delivery_attempts",
                columns: new[] { "tenant_id", "delivery_id", "number" });
        }
    }
}
