using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class HoldsAndDryRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Correlation holds (phase-5-plan p5-d3) and dry run (I67). Every column is nullable or has a constant default: no table rewrite and no
            // backfill. One hold per correlation: its start and its reason are set together (ck_correlations_hold).
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "held_at",
                schema: "notifications",
                table: "escalations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "dry_run",
                schema: "notifications",
                table: "deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "held_at",
                schema: "notifications",
                table: "correlations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "held_by",
                schema: "notifications",
                table: "correlations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hold_reason",
                schema: "notifications",
                table: "correlations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_correlations_hold",
                schema: "notifications",
                table: "correlations",
                sql: "(held_at IS NULL) = (hold_reason IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_correlations_hold",
                schema: "notifications",
                table: "correlations");

            migrationBuilder.DropColumn(
                name: "held_at",
                schema: "notifications",
                table: "escalations");

            migrationBuilder.DropColumn(
                name: "dry_run",
                schema: "notifications",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "held_at",
                schema: "notifications",
                table: "correlations");

            migrationBuilder.DropColumn(
                name: "held_by",
                schema: "notifications",
                table: "correlations");

            migrationBuilder.DropColumn(
                name: "hold_reason",
                schema: "notifications",
                table: "correlations");
        }
    }
}
