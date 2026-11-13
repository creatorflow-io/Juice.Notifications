using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RouteTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "route_targets",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_route_targets", x => new { x.tenant_id, x.route_id, x.target_id });
                    table.ForeignKey(
                        name: "fk_route_targets_integration_targets",
                        columns: x => new { x.tenant_id, x.target_id },
                        principalSchema: "notifications",
                        principalTable: "integration_targets",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_route_targets_routes",
                        columns: x => new { x.tenant_id, x.route_id },
                        principalSchema: "notifications",
                        principalTable: "routes",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_route_targets_tenant_id_target_id",
                schema: "notifications",
                table: "route_targets",
                columns: new[] { "tenant_id", "target_id" });

            // The targets routes already name, at their place in the list. A key that names no target (the race deferred #8 describes) gets no
            // row: there is nothing to keep; the target is linked if it is created later.
            migrationBuilder.Sql("""
                INSERT INTO notifications.route_targets (tenant_id, route_id, target_id, position)
                SELECT r.tenant_id, r.id, t.id, min(k.position) - 1
                FROM notifications.routes r
                CROSS JOIN LATERAL unnest(r.targets) WITH ORDINALITY AS k(key, position)
                JOIN notifications.integration_targets t ON t.tenant_id = r.tenant_id AND t.key = k.key
                GROUP BY r.tenant_id, r.id, t.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "route_targets",
                schema: "notifications");
        }
    }
}
