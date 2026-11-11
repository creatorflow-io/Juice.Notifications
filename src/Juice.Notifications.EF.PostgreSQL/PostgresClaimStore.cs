using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Juice.Notifications.Data.PostgreSQL
{
    /// <summary>
    /// Claims with one statement (design §11, phase-1-plan I6):
    /// <c>UPDATE … SET claimed_by, lease_until, version + 1 WHERE (tenant_id, id) IN (SELECT … WHERE due ORDER BY next_at LIMIT n FOR UPDATE SKIP LOCKED)
    /// RETURNING tenant_id, id, version</c>. <c>SKIP LOCKED</c> keeps two workers off the same row; the version they get back fences every later write.
    /// Table and column names come from the EF model, so a rename can't leave this SQL behind.
    /// </summary>
    public sealed class PostgresClaimStore(NotificationsDbContext db) : IClaimStore
    {
        private static readonly ConcurrentDictionary<Type, string> Statements = new();

        public async Task<IReadOnlyList<Claim>> ClaimAsync<T>(string worker, int batch, TimeSpan lease, DateTimeOffset now, CancellationToken ct)
            where T : Claimable, ITenantOwned
        {
            var sql = Statements.GetOrAdd(typeof(T), _ => Statement(db.Model.FindEntityType(typeof(T))
                ?? throw new InvalidOperationException($"{typeof(T).Name} is not in the model.")));
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var opened = connection.State != System.Data.ConnectionState.Open;
            if (opened)
            {
                await connection.OpenAsync(ct);
            }
            try
            {
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("worker", worker);
                command.Parameters.AddWithValue("now", now);
                command.Parameters.AddWithValue("lease_until", now + lease);
                command.Parameters.AddWithValue("batch", batch);
                var claims = new List<Claim>();
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    claims.Add(new Claim(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2)));
                }
                return claims;
            }
            finally
            {
                if (opened)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private static string Statement(IEntityType entity)
        {
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            string Column(string property) => Quote(entity.FindProperty(property)!.GetColumnName(table)!);
            var name = $"{Quote(entity.GetSchema()!)}.{Quote(entity.GetTableName()!)}";
            var (tenant, id, version) = (Column(nameof(ITenantOwned.TenantId)), Column("Id"), Column(nameof(Claimable.Version)));
            var (claimedBy, leaseUntil, nextAt) = (Column(nameof(Claimable.ClaimedBy)), Column(nameof(Claimable.LeaseUntil)), Column(nameof(Claimable.NextAt)));
            return $"""
                UPDATE {name} AS t
                SET {claimedBy} = @worker, {leaseUntil} = @lease_until, {version} = t.{version} + 1
                WHERE (t.{tenant}, t.{id}) IN (
                    SELECT d.{tenant}, d.{id} FROM {name} AS d
                    WHERE d.{nextAt} <= @now AND (d.{leaseUntil} IS NULL OR d.{leaseUntil} < @now)
                    ORDER BY d.{nextAt}
                    LIMIT @batch
                    FOR UPDATE SKIP LOCKED)
                RETURNING t.{tenant}, t.{id}, t.{version}
                """;
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
