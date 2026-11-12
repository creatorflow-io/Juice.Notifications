using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Juice.Notifications.Data.PostgreSQL
{
    /// <summary>
    /// Claims with one statement (design §11, phase-1-plan I6): a materialized CTE picks the due rows <c>FOR UPDATE SKIP LOCKED</c>, and the update
    /// gives them the worker's name, a lease and <c>version + 1</c>, returning <c>tenant_id, id, version</c>. <c>SKIP LOCKED</c> keeps two workers
    /// off the same row; the version they get back fences every later write. Leases use the database's <c>now()</c>.
    /// Table and column names come from the EF model, so a rename can't leave this SQL behind.
    /// </summary>
    public sealed class PostgresClaimStore(NotificationsDbContext db) : IClaimStore
    {
        private sealed record Sql(string Claim, string Renew, string Release);

        // Per model and entity: another model (a test context, another schema) may map the same entity elsewhere.
        private static readonly ConcurrentDictionary<(IModel, Type), Sql> Statements = new();

        public async Task<IReadOnlyList<Claim>> ClaimAsync<T>(string worker, int batch, TimeSpan lease, DateTimeOffset now, CancellationToken ct)
            where T : Claimable, ITenantOwned
            => await RunAsync(For<T>().Claim, command =>
            {
                command.Parameters.AddWithValue("worker", worker);
                command.Parameters.AddWithValue("now", now);
                command.Parameters.AddWithValue("lease", lease);
                command.Parameters.AddWithValue("batch", batch);
            }, reader => new Claim(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2)), ct);

        public async Task<bool> RenewAsync<T>(Claim claim, TimeSpan lease, CancellationToken ct) where T : Claimable, ITenantOwned
            => (await RunAsync(For<T>().Renew, command =>
            {
                Key(command, claim);
                command.Parameters.AddWithValue("lease", lease);
            }, reader => reader.GetInt32(0), ct)).Count == 1;

        public async Task<Released?> ReleaseAsync<T>(Claim claim, DateTimeOffset? nextAt, FailureBackoff? failure, CancellationToken ct)
            where T : Claimable, ITenantOwned
            => (await RunAsync(For<T>().Release, command =>
            {
                Key(command, claim);
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset?>("next_at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { TypedValue = nextAt });
                command.Parameters.AddWithValue("failed", failure is not null);
                command.Parameters.AddWithValue("now", failure?.Now ?? DateTimeOffset.UnixEpoch);
                command.Parameters.AddWithValue("backoff", failure?.Base ?? TimeSpan.Zero);
                command.Parameters.AddWithValue("backoff_max", failure?.Max ?? TimeSpan.Zero);
            }, reader => new Released(reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)), ct))
                .SingleOrDefault();

        private static void Key(NpgsqlCommand command, Claim claim)
        {
            command.Parameters.AddWithValue("tenant", claim.TenantId);
            command.Parameters.AddWithValue("id", claim.Id);
            command.Parameters.AddWithValue("version", claim.Version);
        }

        private Sql For<T>() => Statements.GetOrAdd((db.Model, typeof(T)), key => Build(key.Item1.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not in the model.")));

        private async Task<List<TResult>> RunAsync<TResult>(string sql, Action<NpgsqlCommand> parameters, Func<NpgsqlDataReader, TResult> read,
            CancellationToken ct)
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var opened = connection.State != System.Data.ConnectionState.Open;
            if (opened)
            {
                await connection.OpenAsync(ct);
            }
            try
            {
                await using var command = new NpgsqlCommand(sql, connection);
                parameters(command);
                var results = new List<TResult>();
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    results.Add(read(reader));
                }
                return results;
            }
            finally
            {
                if (opened)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private static Sql Build(IEntityType entity)
        {
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            string Column(string property) => Quote(entity.FindProperty(property)!.GetColumnName(table)!);
            var name = $"{Quote(entity.GetSchema()!)}.{Quote(entity.GetTableName()!)}";
            var (tenant, id, version) = (Column(nameof(ITenantOwned.TenantId)), Column(nameof(ITenantOwned.Id)), Column(nameof(Claimable.Version)));
            var (claimedBy, leaseUntil, nextAt) = (Column(nameof(Claimable.ClaimedBy)), Column(nameof(Claimable.LeaseUntil)), Column(nameof(Claimable.NextAt)));
            var failures = Column(nameof(Claimable.StepFailures));
            var key = $"t.{tenant} = @tenant AND t.{id} = @id AND t.{version} = @version";
            // Rows with a priority (deliveries) are claimed most urgent first, then by when they fell due (phase-1-plan M7). The column holds the
            // enum's name, so it is ranked explicitly: sorted as text, Critical would come before High but Low before Normal.
            var order = entity.FindProperty("Priority") is { } priority
                ? "CASE d." + Quote(priority.GetColumnName(table)!) + " " + string.Join(" ", Enum.GetValues<NotificationPriority>()
                    .OrderByDescending(p => p).Select((p, rank) => $"WHEN '{p}' THEN {rank}")) + $" ELSE {Enum.GetValues<NotificationPriority>().Length} END, d.{nextAt}"
                : $"d.{nextAt}";
            return new Sql(
                // MATERIALIZED: the locking subquery runs once, whatever plan the update gets.
                Claim: $"""
                    WITH due AS MATERIALIZED (
                        SELECT d.{tenant}, d.{id} FROM {name} AS d
                        WHERE d.{nextAt} <= @now AND (d.{leaseUntil} IS NULL OR d.{leaseUntil} < now())
                        ORDER BY {order}
                        LIMIT @batch
                        FOR UPDATE SKIP LOCKED)
                    UPDATE {name} AS t
                    SET {claimedBy} = @worker, {leaseUntil} = now() + @lease, {version} = t.{version} + 1
                    FROM due
                    WHERE t.{tenant} = due.{tenant} AND t.{id} = due.{id}
                    RETURNING t.{tenant}, t.{id}, t.{version}
                    """,
                // The version is the fence: a row claimed again since doesn't match, and its new owner's lease stands.
                Renew: $"""
                    UPDATE {name} AS t SET {leaseUntil} = now() + @lease
                    WHERE {key}
                    RETURNING t.{version}
                    """,
                // A failure's backoff is computed here, from the count it raises, so the row is never handed back due now in between.
                Release: $"""
                    UPDATE {name} AS t
                    SET {claimedBy} = NULL, {leaseUntil} = NULL, {version} = t.{version} + 1,
                        {nextAt} = CASE WHEN @failed
                            THEN @now + LEAST(@backoff * power(2, LEAST(t.{failures}, 20)), @backoff_max)
                            ELSE COALESCE(@next_at, t.{nextAt}) END,
                        {failures} = CASE WHEN @failed THEN t.{failures} + 1 ELSE t.{failures} END
                    WHERE {key}
                    RETURNING t.{version}, t.{failures}, t.{nextAt}
                    """);
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public static class PostgresClaimStoreExtensions
    {
        /// <summary>Claims on PostgreSQL.</summary>
        public static IServiceCollection AddPostgresClaimStore(this IServiceCollection services)
        {
            services.TryAddScoped<IClaimStore, PostgresClaimStore>();
            return services;
        }
    }
}
