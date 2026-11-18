using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ChatLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "link_codes",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    contact_point_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_codes", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_link_codes_subscribers_tenant_id_subscriber_id",
                        columns: x => new { x.tenant_id, x.subscriber_id },
                        principalSchema: "notifications",
                        principalTable: "subscribers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_link_codes_expires_at",
                schema: "notifications",
                table: "link_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_link_codes_once",
                schema: "notifications",
                table: "link_codes",
                columns: new[] { "tenant_id", "code_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_link_codes_tenant_id_subscriber_id_created_at",
                schema: "notifications",
                table: "link_codes",
                columns: new[] { "tenant_id", "subscriber_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "link_codes",
                schema: "notifications");
        }
    }
}
