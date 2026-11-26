using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class SendHealthIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_at",
                schema: "notifications",
                table: "delivery_attempts",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_in_flight",
                schema: "notifications",
                table: "deliveries",
                column: "created_at",
                filter: "status IN ('Pending', 'Sending', 'Retrying')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_delivery_attempts_at",
                schema: "notifications",
                table: "delivery_attempts");

            migrationBuilder.DropIndex(
                name: "ix_deliveries_in_flight",
                schema: "notifications",
                table: "deliveries");
        }
    }
}
