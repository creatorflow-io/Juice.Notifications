using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowRunner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "step_entered_at",
                schema: "notifications",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "throttle_entries",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step = table.Column<int>(type: "integer", nullable: false),
                    type_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_throttle_entries", x => new { x.tenant_id, x.notification_id, x.step });
                    table.ForeignKey(
                        name: "fk_throttle_entries_notifications_tenant_id_notification_id",
                        columns: x => new { x.tenant_id, x.notification_id },
                        principalSchema: "notifications",
                        principalTable: "notifications",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_throttle_entries_window",
                schema: "notifications",
                table: "throttle_entries",
                columns: new[] { "tenant_id", "type_key", "key", "window_start" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "throttle_entries",
                schema: "notifications");

            migrationBuilder.DropColumn(
                name: "step_entered_at",
                schema: "notifications",
                table: "notifications");
        }
    }
}
