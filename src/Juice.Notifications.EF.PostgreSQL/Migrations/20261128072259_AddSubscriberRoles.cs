using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriberRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "subscriber_roles",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriber_roles", x => new { x.tenant_id, x.subscriber_id, x.role });
                    table.ForeignKey(
                        name: "fk_subscriber_roles_subscribers",
                        columns: x => new { x.tenant_id, x.subscriber_id },
                        principalSchema: "notifications",
                        principalTable: "subscribers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscriber_roles_tenant_id_role_seen_at",
                schema: "notifications",
                table: "subscriber_roles",
                columns: new[] { "tenant_id", "role", "seen_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subscriber_roles",
                schema: "notifications");
        }
    }
}
