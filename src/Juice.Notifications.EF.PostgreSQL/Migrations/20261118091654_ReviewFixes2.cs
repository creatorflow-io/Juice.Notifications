using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ReviewFixes2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "failure_check_due",
                schema: "notifications",
                table: "deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_failure_check",
                schema: "notifications",
                table: "deliveries",
                column: "id",
                filter: "failure_check_due");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deliveries_failure_check",
                schema: "notifications",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "failure_check_due",
                schema: "notifications",
                table: "deliveries");
        }
    }
}
