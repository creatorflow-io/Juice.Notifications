using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <summary>
    /// M50: the webhooks alias is retired, so its two owners ('webhooks-alias' and 'alias-config') become the API's. Data only, no schema change.
    /// Idempotent: the second run finds no row. Rows owned by 'config' or 'service' are never touched; each row keeps its own tenant.
    /// </summary>
    public partial class RetireAliasOwners : Migration
    {
        private static readonly string[] Tables =
            ["integrations", "integration_targets", "routes", "destination_allowlist", "escalation_policies"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"UPDATE notifications.{table} SET managed_by = 'api' WHERE managed_by IN ('webhooks-alias', 'alias-config');");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The two owners are gone from the code; which rows had them is not kept, so nothing is restored.
        }
    }
}
