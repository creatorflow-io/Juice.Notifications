using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class DisplayGroupsAndInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "display_groups",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    built_in = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_display_groups", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_display_groups_key", x => new { x.tenant_id, x.key });
                });

            migrationBuilder.CreateTable(
                name: "inbox_items",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    group_key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    title = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    actions = table.Column<string>(type: "jsonb", nullable: true),
                    actionable = table.Column<bool>(type: "boolean", nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_items", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_inbox_items_deliveries_tenant_id_delivery_id",
                        columns: x => new { x.tenant_id, x.delivery_id },
                        principalSchema: "notifications",
                        principalTable: "deliveries",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_inbox_items_subscribers_tenant_id_subscriber_id",
                        columns: x => new { x.tenant_id, x.subscriber_id },
                        principalSchema: "notifications",
                        principalTable: "subscribers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "type_display_groups",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    group_key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_type_display_groups", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_type_display_groups_display_groups",
                        columns: x => new { x.tenant_id, x.group_key },
                        principalSchema: "notifications",
                        principalTable: "display_groups",
                        principalColumns: new[] { "tenant_id", "key" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_types_key_version",
                schema: "notifications",
                table: "notification_types",
                columns: new[] { "key", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_items_delivery",
                schema: "notifications",
                table: "inbox_items",
                columns: new[] { "tenant_id", "delivery_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbox_items_group",
                schema: "notifications",
                table: "inbox_items",
                columns: new[] { "tenant_id", "group_key" },
                filter: "archived_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_items_open",
                schema: "notifications",
                table: "inbox_items",
                columns: new[] { "tenant_id", "subscriber_id", "group_key", "id" },
                filter: "archived_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_items_person",
                schema: "notifications",
                table: "inbox_items",
                columns: new[] { "tenant_id", "subscriber_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_items_type",
                schema: "notifications",
                table: "inbox_items",
                columns: new[] { "tenant_id", "type_key" },
                filter: "archived_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_type_display_groups_key",
                schema: "notifications",
                table: "type_display_groups",
                columns: new[] { "tenant_id", "type_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_type_display_groups_tenant_id_group_key",
                schema: "notifications",
                table: "type_display_groups",
                columns: new[] { "tenant_id", "group_key" });
            // Where a type's inbox items go in a tenant (phase-2-plan I24): the tenant's choice, else the group the type's latest version declares
            // when the tenant has it, else general. One definition for writing, restoring and re-homing items (Inbox.DisplayGroupCatalog).
            migrationBuilder.Sql(Data.InboxSql.GroupFunction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_items",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "type_display_groups",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "display_groups",
                schema: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_notification_types_key_version",
                schema: "notifications",
                table: "notification_types");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS notifications.inbox_group(uuid, text);");
        }
    }
}
