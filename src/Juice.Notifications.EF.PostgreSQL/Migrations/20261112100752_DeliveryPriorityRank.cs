using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryPriorityRank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The names become the enum's values (NotificationPriority: Low 0, Normal 1, High 2, Critical 3), so the claim index orders by them.
            migrationBuilder.Sql("""
                ALTER TABLE notifications.deliveries ALTER COLUMN priority TYPE integer USING CASE priority
                    WHEN 'Low' THEN 0 WHEN 'Normal' THEN 1 WHEN 'High' THEN 2 WHEN 'Critical' THEN 3 ELSE 1 END;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_claim",
                schema: "notifications",
                table: "deliveries",
                columns: new[] { "priority", "next_at" },
                descending: new[] { true, false },
                filter: "next_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deliveries_claim",
                schema: "notifications",
                table: "deliveries");

            migrationBuilder.Sql("""
                ALTER TABLE notifications.deliveries ALTER COLUMN priority TYPE character varying(20) USING CASE priority
                    WHEN 0 THEN 'Low' WHEN 1 THEN 'Normal' WHEN 2 THEN 'High' WHEN 3 THEN 'Critical' ELSE 'Normal' END;
                """);
        }
    }
}
