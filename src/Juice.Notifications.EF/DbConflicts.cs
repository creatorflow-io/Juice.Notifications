using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Juice.Notifications.Data
{
    /// <summary>Constraint names the API translates into answers. The model sets these names, so a rename can't silently break a translation.</summary>
    public static class Constraints
    {
        public const string ApplicationKey = "ix_applications_key";
        public const string ApplicationClientId = "ix_applications_client_id";
        public const string ApplicationTenantLink = "pk_application_tenants";
        public const string RequestApplicationTenant = "fk_requests_application_tenants_application_id_tenant_id";
        public const string TypeVersion = "ix_notification_types_application_id_key_version";
        public const string RequestIdempotencyKey = "ix_requests_tenant_id_application_id_idempotency_key";
        public const string RecipientNamed = "ix_notification_recipients_named";
        public const string AttemptNumber = "ix_delivery_attempts_number";
        public const string TargetIntegration = "fk_integration_targets_integrations_tenant_id_integration_id";
        public const string DeliveryIntegration = "fk_deliveries_integrations_tenant_id_integration_id";
        public const string AttemptIntegration = "fk_delivery_attempts_integrations_tenant_id_integration_id";
    }

    public enum ConflictKind
    {
        /// <summary>A unique key was taken: <see cref="DbConflict.Constraint"/> says which.</summary>
        Unique,
        /// <summary>A referenced row was missing, or a referencing row stopped a delete: <see cref="DbConflict.Constraint"/> says which.</summary>
        ForeignKey,
        /// <summary>An update or delete found its row already gone or changed.</summary>
        RowGone,
        /// <summary>PostgreSQL aborted one of two transactions that waited on each other (deadlock) or couldn't be serialized.</summary>
        Aborted,
    }

    /// <summary>Another transaction changed the same rows first.</summary>
    public sealed record DbConflict(ConflictKind Kind, string? Constraint);

    /// <summary>
    /// Recognizes a race with another transaction in any failure: from a save, a query or the commit, however it is wrapped. Provider-specific, so it
    /// lives with the provider. The caller translates the conflict into its answer; nothing is run again.
    /// <para>Commit failures arrive intact only from Juice.EF after 10.12.0: 10.12.0's <c>UnitOfWork.CommitTransactionAsync</c> rolls back a
    /// transaction Npgsql has already completed, and that error replaces the real one (docs/deferred.md #2). Nothing in phase 1 fails at commit:
    /// there are no deferred constraints, and transactions run at read committed.</para>
    /// </summary>
    public static class DbConflicts
    {
        public static DbConflict? Classify(Exception ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
            {
                switch (e)
                {
                    case DbUpdateConcurrencyException:
                        return new DbConflict(ConflictKind.RowGone, null);
                    case PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg:
                        return new DbConflict(ConflictKind.Unique, pg.ConstraintName);
                    case PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } pg:
                        return new DbConflict(ConflictKind.ForeignKey, pg.ConstraintName);
                    case PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure }:
                        return new DbConflict(ConflictKind.Aborted, null);
                    case PostgresException:
                        return null;
                }
            }
            return null;
        }

        /// <summary>
        /// The connection to the database failed: it couldn't be opened (refused, timed out, no connection free in the pool), broke, or the
        /// server is shutting down or full. Nothing about the work being done. Not whatever Npgsql calls transient: a deadlock or serialization
        /// failure is the work colliding with other work, and a command timeout is the work's own query taking too long; both are the work's to
        /// count. An I/O error outside Npgsql (a file, an HTTP call) is the work's too.
        /// </summary>
        public static bool IsConnectionFailure(Exception ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
            {
                switch (e)
                {
                    // The server answered: a connection-class state means it is going away or full, anything else is about the work.
                    case PostgresException pg:
                        return pg.SqlState.StartsWith("08", StringComparison.Ordinal) || pg.SqlState is PostgresErrorCodes.AdminShutdown
                            or PostgresErrorCodes.CrashShutdown or PostgresErrorCodes.CannotConnectNow or PostgresErrorCodes.TooManyConnections;
                    // Opening a connection failed, including a timeout while connecting or waiting for the pool.
                    case NpgsqlException when e.Message.StartsWith("Failed to connect", StringComparison.Ordinal)
                        || e.Message.StartsWith("The connection pool has been exhausted", StringComparison.Ordinal):
                        return true;
                    // An open connection broke; but a timeout under it is a command that ran too long.
                    case NpgsqlException { IsTransient: true } when !Chain(e).Any(inner => inner is TimeoutException):
                        return true;
                }
            }
            return false;
        }

        private static IEnumerable<Exception> Chain(Exception ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
            {
                yield return e;
            }
        }
    }
}
