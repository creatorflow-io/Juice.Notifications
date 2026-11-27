using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class HealthHeldAndSettingsVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deliveries_in_flight",
                schema: "notifications",
                table: "deliveries");

            migrationBuilder.AddColumn<int>(
                name: "version",
                schema: "notifications",
                table: "tenant_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "tenant_held",
                schema: "notifications",
                table: "deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_in_flight",
                schema: "notifications",
                table: "deliveries",
                column: "created_at",
                filter: "status IN ('Pending', 'Sending', 'Retrying') AND NOT tenant_held");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_tenant_held",
                schema: "notifications",
                table: "deliveries",
                columns: new[] { "next_at", "created_at" },
                filter: "tenant_held AND status IN ('Pending', 'Sending', 'Retrying')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deliveries_in_flight",
                schema: "notifications",
                table: "deliveries");

            migrationBuilder.DropIndex(
                name: "ix_deliveries_tenant_held",
                schema: "notifications",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "notifications",
                table: "tenant_settings");

            migrationBuilder.DropColumn(
                name: "tenant_held",
                schema: "notifications",
                table: "deliveries");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_in_flight",
                schema: "notifications",
                table: "deliveries",
                column: "created_at",
                filter: "status IN ('Pending', 'Sending', 'Retrying')");
        }
    }
}
