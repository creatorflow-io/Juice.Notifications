using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ContactPointsAndDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verification_window_start",
                schema: "notifications",
                table: "subscribers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "verifications_in_window",
                schema: "notifications",
                table: "subscribers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "contact_points",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    address = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    address_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    masked = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verified_by = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    health = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    health_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    health_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_points", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_contact_points_subscribers",
                        columns: x => new { x.tenant_id, x.subscriber_id },
                        principalSchema: "notifications",
                        principalTable: "subscribers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_registrations",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    app_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    token = table.Column<string>(type: "character varying(6000)", maxLength: 6000, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_registrations", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_device_registrations_subscribers_tenant_id_subscriber_id",
                        columns: x => new { x.tenant_id, x.subscriber_id },
                        principalSchema: "notifications",
                        principalTable: "subscribers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contact_verifications",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_point_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tries = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_verifications", x => new { x.tenant_id, x.contact_point_id });
                    table.ForeignKey(
                        name: "fk_contact_verifications_contact_points",
                        columns: x => new { x.tenant_id, x.contact_point_id },
                        principalSchema: "notifications",
                        principalTable: "contact_points",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contact_points_address",
                schema: "notifications",
                table: "contact_points",
                columns: new[] { "tenant_id", "subscriber_id", "channel", "address_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_contact_points_tenant_id_address_hash",
                schema: "notifications",
                table: "contact_points",
                columns: new[] { "tenant_id", "address_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_contact_verifications_expires_at",
                schema: "notifications",
                table: "contact_verifications",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_device_registrations_last_seen_at",
                schema: "notifications",
                table: "device_registrations",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_device_registrations_tenant_id_subscriber_id",
                schema: "notifications",
                table: "device_registrations",
                columns: new[] { "tenant_id", "subscriber_id" });

            migrationBuilder.CreateIndex(
                name: "ix_device_registrations_token",
                schema: "notifications",
                table: "device_registrations",
                columns: new[] { "tenant_id", "platform", "app_id", "token_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_verifications",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "device_registrations",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "contact_points",
                schema: "notifications");

            migrationBuilder.DropColumn(
                name: "verification_window_start",
                schema: "notifications",
                table: "subscribers");

            migrationBuilder.DropColumn(
                name: "verifications_in_window",
                schema: "notifications",
                table: "subscribers");
        }
    }
}
