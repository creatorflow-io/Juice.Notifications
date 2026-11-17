using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class Escalations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "escalation_specs",
                schema: "notifications",
                table: "notifications",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "escalation_step",
                schema: "notifications",
                table: "notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "escalation_policies",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    stop_on_acknowledge = table.Column<bool>(type: "boolean", nullable: false),
                    managed_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    definition_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_escalation_policies", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "escalations",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_step = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    stop_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    claimed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    step_failures = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_escalations", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_escalations_correlations_tenant_id_correlation_id",
                        columns: x => new { x.tenant_id, x.correlation_id },
                        principalSchema: "notifications",
                        principalTable: "correlations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_escalations_notifications_tenant_id_notification_id",
                        columns: x => new { x.tenant_id, x.notification_id },
                        principalSchema: "notifications",
                        principalTable: "notifications",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_escalation_policies_key",
                schema: "notifications",
                table: "escalation_policies",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_escalations_correlation",
                schema: "notifications",
                table: "escalations",
                columns: new[] { "tenant_id", "correlation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_escalations_due",
                schema: "notifications",
                table: "escalations",
                column: "next_at",
                filter: "next_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_escalations_notification",
                schema: "notifications",
                table: "escalations",
                columns: new[] { "tenant_id", "notification_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "escalation_policies",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "escalations",
                schema: "notifications");

            migrationBuilder.DropColumn(
                name: "escalation_specs",
                schema: "notifications",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "escalation_step",
                schema: "notifications",
                table: "notifications");
        }
    }
}
