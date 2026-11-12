using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <summary>
    /// Each delivery item names its request (through its recipient's notification), so the dispatcher reaches the request in one indexed step.
    /// Existing items are filled in from that chain before the column becomes required.
    /// </summary>
    public partial class DeliveryItemRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "request_id",
                schema: "notifications",
                table: "delivery_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE notifications.delivery_items AS i
                SET request_id = n.request_id
                FROM notifications.notification_recipients AS r
                JOIN notifications.notifications AS n ON n.tenant_id = r.tenant_id AND n.id = r.notification_id
                WHERE r.tenant_id = i.tenant_id AND r.id = i.recipient_id;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "request_id",
                schema: "notifications",
                table: "delivery_items",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_delivery_items_tenant_id_request_id",
                schema: "notifications",
                table: "delivery_items",
                columns: new[] { "tenant_id", "request_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_delivery_items_requests_tenant_id_request_id",
                schema: "notifications",
                table: "delivery_items",
                columns: new[] { "tenant_id", "request_id" },
                principalSchema: "notifications",
                principalTable: "requests",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_delivery_items_requests_tenant_id_request_id",
                schema: "notifications",
                table: "delivery_items");

            migrationBuilder.DropIndex(
                name: "ix_delivery_items_tenant_id_request_id",
                schema: "notifications",
                table: "delivery_items");

            migrationBuilder.DropColumn(
                name: "request_id",
                schema: "notifications",
                table: "delivery_items");
        }
    }
}
