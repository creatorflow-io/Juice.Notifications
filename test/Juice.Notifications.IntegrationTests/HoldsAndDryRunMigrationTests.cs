using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// QA (M44-0): the <c>HoldsAndDryRun</c> migration applied over release/0's schema with rows already there, reverted, and applied again
    /// (m44-architecture §2: every column nullable or defaulted, no backfill, <c>ck_correlations_hold</c> holds for existing rows).
    /// </summary>
    public sealed class HoldsAndDryRunMigrationTests : IAsyncLifetime
    {
        private const string Previous = "20261119064309_ConsoleReads";
        private const string Target = "20261126025035_HoldsAndDryRun";
        private static readonly Guid Tenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000000c1");
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly ScratchDatabase _database = new();

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        [Fact]
        public async Task The_migration_applies_over_existing_rows_reverts_and_applies_again()
        {
            LocalStack.RequireAvailable();
            // Release/0's rows: a delivery chain (seeded on the full model, then the migration reverted under it) and correlations written
            // against the old schema.
            Guid delivery;
            Guid application;
            await using (var db = await _database.MigratedAsync())
            {
                var app = Seed.Application("qa-migration");
                var type = Seed.Type(app.Id, "qa-migration.thing");
                var integration = Seed.Integration(Tenant);
                var request = Seed.Request(Tenant, app.Id, type.Id);
                var notification = Seed.Notification(Tenant, request.Id);
                var recipient = Seed.Recipient(Tenant, notification.Id);
                var seeded = Seed.Delivery(Tenant, integration.Id);
                db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now },
                    integration, request, notification, recipient, seeded, Seed.Item(Tenant, seeded.Id, recipient.Id, request.Id));
                await db.SaveChangesAsync(Ct);
                delivery = seeded.Id;
                application = app.Id;
                await db.GetService<IMigrator>().MigrateAsync(Previous, cancellationToken: Ct);
            }

            Assert.False(await ColumnExistsAsync("correlations", "held_at"));
            Assert.False(await ColumnExistsAsync("correlations", "hold_reason"));
            Assert.False(await ColumnExistsAsync("correlations", "held_by"));
            Assert.False(await ColumnExistsAsync("escalations", "held_at"));
            Assert.False(await ColumnExistsAsync("deliveries", "dry_run"));
            Assert.Null(await ConstraintValidatedAsync("ck_correlations_hold"));

            await ExecuteAsync($"""
                INSERT INTO notifications.correlations (tenant_id, id, application_id, key, state, acknowledged_by, acknowledged_at, resolved_at,
                    outcome, resolved_by, reopened_at, created_at, last_request_at, updated_at)
                VALUES ('{Tenant}', '{Guid.CreateVersion7()}', '{application}', 'qa-open', 'Open', NULL, NULL, NULL, NULL, NULL, NULL, now(), now(), now()),
                       ('{Tenant}', '{Guid.CreateVersion7()}', '{application}', 'qa-ack', 'Acknowledged', 'ops', now(), NULL, NULL, NULL, NULL, now(), now(), now()),
                       ('{Tenant}', '{Guid.CreateVersion7()}', '{application}', 'qa-resolved', 'Resolved', NULL, NULL, now(), 'fixed', 'ops', NULL, now(), now(), now())
                """);

            for (var round = 0; round < 2; round++)
            {
                await using (var db = _database.Context())
                {
                    await db.Database.MigrateAsync(Ct);
                    Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
                    // Applied, whatever migrations came after it.
                    Assert.Contains(Target, await db.Database.GetAppliedMigrationsAsync(Ct));

                    var correlations = await db.Correlations.AsNoTracking().Where(c => c.TenantId == Tenant).ToListAsync(Ct);
                    Assert.Equal(3, correlations.Count);
                    Assert.All(correlations, c => Assert.True(c.HeldAt is null && c.HoldReason is null && c.HeldBy is null, c.Key));
                    Assert.False((await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == delivery, Ct)).DryRun);
                }

                // The constraint is validated against the existing rows (not NOT VALID), and enforced on them.
                Assert.True(await ConstraintValidatedAsync("ck_correlations_hold"));
                var halfHold = await Assert.ThrowsAsync<PostgresException>(
                    () => ExecuteAsync("UPDATE notifications.correlations SET held_at = now() WHERE key = 'qa-open'"));
                Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_correlations_hold"), (halfHold.SqlState, halfHold.ConstraintName));
                var halfReason = await Assert.ThrowsAsync<PostgresException>(
                    () => ExecuteAsync("UPDATE notifications.correlations SET hold_reason = 'silence:7' WHERE key = 'qa-ack'"));
                Assert.Equal(PostgresErrorCodes.CheckViolation, halfReason.SqlState);
                await ExecuteAsync("UPDATE notifications.correlations SET held_at = now(), hold_reason = 'silence:7' WHERE key = 'qa-resolved'");
                // held_by alone is allowed: the constraint covers the start and the reason only.
                await ExecuteAsync("UPDATE notifications.correlations SET held_by = 'ops' WHERE key = 'qa-open'");

                Assert.Equal(("NO", "false"), await ColumnAsync("deliveries", "dry_run"));
                Assert.Equal(("YES", null), await ColumnAsync("escalations", "held_at"));
                Assert.Equal(200, await LengthAsync("correlations", "hold_reason"));
                Assert.Equal(200, await LengthAsync("correlations", "held_by"));
                // managed_by widened for the webhooks alias (14 characters) on every table that has it.
                foreach (var table in new[] { "integrations", "integration_targets", "routes", "destination_allowlist", "recipient_resolvers", "escalation_policies" })
                {
                    Assert.Equal(20, await LengthAsync(table, "managed_by"));
                }

                // Down: a held row doesn't block it, and the columns and the constraint go.
                await using (var db = _database.Context())
                {
                    await db.GetService<IMigrator>().MigrateAsync(Previous, cancellationToken: Ct);
                }
                Assert.False(await ColumnExistsAsync("correlations", "held_at"));
                Assert.False(await ColumnExistsAsync("deliveries", "dry_run"));
                Assert.Null(await ConstraintValidatedAsync("ck_correlations_hold"));
                Assert.Equal(10, await LengthAsync("routes", "managed_by"));
            }
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        private async Task<T?> ScalarAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            var value = await command.ExecuteScalarAsync(Ct);
            return value is null or DBNull ? default : (T)value;
        }

        private async Task<bool> ColumnExistsAsync(string table, string column)
            => await ScalarAsync<long>($"SELECT count(*) FROM information_schema.columns WHERE table_schema = 'notifications' AND table_name = '{table}' AND column_name = '{column}'") == 1;

        private async Task<(string Nullable, string? Default)> ColumnAsync(string table, string column)
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                $"SELECT is_nullable, column_default FROM information_schema.columns WHERE table_schema = 'notifications' AND table_name = '{table}' AND column_name = '{column}'",
                connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct), $"{table}.{column} is missing");
            return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }

        private async Task<int> LengthAsync(string table, string column)
            => await ScalarAsync<int>($"SELECT character_maximum_length FROM information_schema.columns WHERE table_schema = 'notifications' AND table_name = '{table}' AND column_name = '{column}'");

        private Task<bool?> ConstraintValidatedAsync(string name)
            => ScalarAsync<bool?>($"SELECT c.convalidated FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'notifications' AND c.conname = '{name}'");
    }
}
