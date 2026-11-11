using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class TypeCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "correlation",
                schema: "notifications",
                table: "notification_types",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "optional"); // types declared before this rule existed allowed a key without requiring one
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "correlation",
                schema: "notifications",
                table: "notification_types");
        }
    }
}
