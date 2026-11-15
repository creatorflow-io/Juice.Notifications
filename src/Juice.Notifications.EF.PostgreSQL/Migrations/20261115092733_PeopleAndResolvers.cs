using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class PeopleAndResolvers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "subscriber_id",
                schema: "notifications",
                table: "notification_recipients",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "via",
                schema: "notifications",
                table: "notification_recipients",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateTable(
                name: "recipient_resolvers",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    handles = table.Column<string[]>(type: "text[]", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    secret_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    managed_by = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    definition_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipient_resolvers", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "subscribers",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    merged_into = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscribers", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_subscribers_subscribers_tenant_id_merged_into",
                        columns: x => new { x.tenant_id, x.merged_into },
                        principalSchema: "notifications",
                        principalTable: "subscribers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscriber_aliases",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    linked_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriber_aliases", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_subscriber_aliases_subscribers",
                        columns: x => new { x.tenant_id, x.subscriber_id },
                        principalSchema: "notifications",
                        principalTable: "subscribers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_recipients_tenant_id_subscriber_id",
                schema: "notifications",
                table: "notification_recipients",
                columns: new[] { "tenant_id", "subscriber_id" });

            migrationBuilder.CreateIndex(
                name: "ix_recipient_resolvers_tenant_id_key",
                schema: "notifications",
                table: "recipient_resolvers",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriber_aliases_identity",
                schema: "notifications",
                table: "subscriber_aliases",
                columns: new[] { "tenant_id", "source", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriber_aliases_tenant_id_subscriber_id",
                schema: "notifications",
                table: "subscriber_aliases",
                columns: new[] { "tenant_id", "subscriber_id" });

            migrationBuilder.CreateIndex(
                name: "ix_subscribers_identity",
                schema: "notifications",
                table: "subscribers",
                columns: new[] { "tenant_id", "source", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscribers_tenant_id_merged_into",
                schema: "notifications",
                table: "subscribers",
                columns: new[] { "tenant_id", "merged_into" });

            migrationBuilder.AddForeignKey(
                name: "fk_notification_recipients_subscribers_tenant_id_subscriber_id",
                schema: "notifications",
                table: "notification_recipients",
                columns: new[] { "tenant_id", "subscriber_id" },
                principalSchema: "notifications",
                principalTable: "subscribers",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_notification_recipients_subscribers_tenant_id_subscriber_id",
                schema: "notifications",
                table: "notification_recipients");

            migrationBuilder.DropTable(
                name: "recipient_resolvers",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "subscriber_aliases",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "subscribers",
                schema: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_notification_recipients_tenant_id_subscriber_id",
                schema: "notifications",
                table: "notification_recipients");

            migrationBuilder.DropColumn(
                name: "subscriber_id",
                schema: "notifications",
                table: "notification_recipients");

            migrationBuilder.DropColumn(
                name: "via",
                schema: "notifications",
                table: "notification_recipients");
        }
    }
}
