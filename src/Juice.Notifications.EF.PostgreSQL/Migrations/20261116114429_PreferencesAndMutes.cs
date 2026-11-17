using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class PreferencesAndMutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "quiet_hours",
                schema: "notifications",
                table: "subscribers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "bypassed",
                schema: "notifications",
                table: "notification_recipients",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateTable(
                name: "mutes",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_id = table.Column<Guid>(type: "uuid", nullable: true),
                    match = table.Column<string>(type: "jsonb", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mutes", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_mutes_subscribers",
                        columns: x => new { x.tenant_id, x.subscriber_id },
                        principalSchema: "notifications",
                        principalTable: "subscribers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "preferences",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_preferences", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_preferences_subscribers",
                        columns: x => new { x.tenant_id, x.subscriber_id },
                        principalSchema: "notifications",
                        principalTable: "subscribers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mutes_tenant_id_subscriber_id",
                schema: "notifications",
                table: "mutes",
                columns: new[] { "tenant_id", "subscriber_id" },
                filter: "cancelled_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_preferences_key",
                schema: "notifications",
                table: "preferences",
                columns: new[] { "tenant_id", "subscriber_id", "category", "channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mutes",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "preferences",
                schema: "notifications");

            migrationBuilder.DropColumn(
                name: "quiet_hours",
                schema: "notifications",
                table: "subscribers");

            migrationBuilder.DropColumn(
                name: "bypassed",
                schema: "notifications",
                table: "notification_recipients");
        }
    }
}
