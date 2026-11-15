using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class LocalGroupsAndTopics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "local_groups",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_local_groups", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "topics",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    self_service = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topics", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "local_group_members",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_local_group_members", x => new { x.tenant_id, x.group_id, x.subscriber_id });
                    table.ForeignKey(
                        name: "fk_local_group_members_local_groups",
                        columns: x => new { x.tenant_id, x.group_id },
                        principalSchema: "notifications",
                        principalTable: "local_groups",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_local_group_members_subscribers_tenant_id_subscriber_id",
                        columns: x => new { x.tenant_id, x.subscriber_id },
                        principalSchema: "notifications",
                        principalTable: "subscribers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "topic_subscriptions",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscribed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topic_subscriptions", x => new { x.tenant_id, x.topic_id, x.subscriber_id });
                    table.ForeignKey(
                        name: "fk_topic_subscriptions_subscribers_tenant_id_subscriber_id",
                        columns: x => new { x.tenant_id, x.subscriber_id },
                        principalSchema: "notifications",
                        principalTable: "subscribers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_topic_subscriptions_topics",
                        columns: x => new { x.tenant_id, x.topic_id },
                        principalSchema: "notifications",
                        principalTable: "topics",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_local_group_members_tenant_id_subscriber_id",
                schema: "notifications",
                table: "local_group_members",
                columns: new[] { "tenant_id", "subscriber_id" });

            migrationBuilder.CreateIndex(
                name: "ix_local_groups_key",
                schema: "notifications",
                table: "local_groups",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_topic_subscriptions_tenant_id_subscriber_id",
                schema: "notifications",
                table: "topic_subscriptions",
                columns: new[] { "tenant_id", "subscriber_id" });

            migrationBuilder.CreateIndex(
                name: "ix_topics_key",
                schema: "notifications",
                table: "topics",
                columns: new[] { "tenant_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_group_members",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "topic_subscriptions",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "local_groups",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "topics",
                schema: "notifications");
        }
    }
}
