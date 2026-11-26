using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RequestDryStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_digests_open",
                schema: "notifications",
                table: "digests");

            migrationBuilder.AddColumn<bool>(
                name: "dry_run",
                schema: "notifications",
                table: "requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "dry_run",
                schema: "notifications",
                table: "digests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_digests_open",
                schema: "notifications",
                table: "digests",
                columns: new[] { "tenant_id", "type_key", "by", "key", "dry_run" },
                unique: true,
                filter: "state = 'Open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down fails while a key has both a dry and a real digest open (the old index allows one): close one of them first.
            migrationBuilder.DropIndex(
                name: "ix_digests_open",
                schema: "notifications",
                table: "digests");

            migrationBuilder.DropColumn(
                name: "dry_run",
                schema: "notifications",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "dry_run",
                schema: "notifications",
                table: "digests");

            migrationBuilder.CreateIndex(
                name: "ix_digests_open",
                schema: "notifications",
                table: "digests",
                columns: new[] { "tenant_id", "type_key", "by", "key" },
                unique: true,
                filter: "state = 'Open'");
        }
    }
}
