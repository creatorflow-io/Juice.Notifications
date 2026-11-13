using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <summary>The RabbitMQ user an application publishes NotificationRequested as (p1-05); unique, and null for HTTP-only applications.</summary>
    public partial class ApplicationBrokerUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "broker_user",
                schema: "notifications",
                table: "applications",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_applications_broker_user",
                schema: "notifications",
                table: "applications",
                column: "broker_user",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_applications_broker_user",
                schema: "notifications",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "broker_user",
                schema: "notifications",
                table: "applications");
        }
    }
}
