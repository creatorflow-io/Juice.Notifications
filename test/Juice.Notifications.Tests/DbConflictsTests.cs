using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Juice.Notifications.Tests
{
    /// <summary>A race is recognized however it arrives: from a save, a query or the commit, wrapped or not.</summary>
    public sealed class DbConflictsTests
    {
        private static PostgresException Pg(string sqlState, string? constraint = null)
            => new("failed", "ERROR", "ERROR", sqlState, constraintName: constraint);

        [Theory]
        [InlineData(PostgresErrorCodes.UniqueViolation, ConflictKind.Unique)]
        [InlineData(PostgresErrorCodes.ForeignKeyViolation, ConflictKind.ForeignKey)]
        [InlineData(PostgresErrorCodes.DeadlockDetected, ConflictKind.Aborted)]
        [InlineData(PostgresErrorCodes.SerializationFailure, ConflictKind.Aborted)]
        public void Each_race_has_its_kind_from_a_save_a_query_the_commit_or_wrapped(string sqlState, ConflictKind kind)
        {
            Assert.Equal(kind, DbConflicts.Classify(new DbUpdateException("save failed", Pg(sqlState)))?.Kind);
            Assert.Equal(kind, DbConflicts.Classify(Pg(sqlState))?.Kind);
            Assert.Equal(kind, DbConflicts.Classify(new InvalidOperationException("commit failed", Pg(sqlState)))?.Kind);
        }

        [Fact]
        public void The_constraint_that_stopped_it_is_kept()
        {
            var conflict = DbConflicts.Classify(new DbUpdateException("save failed", Pg(PostgresErrorCodes.UniqueViolation, Constraints.ApplicationKey)));
            Assert.Equal(new DbConflict(ConflictKind.Unique, Constraints.ApplicationKey), conflict);
        }

        [Fact]
        public void A_delete_or_update_that_found_no_row_is_a_race()
            => Assert.Equal(ConflictKind.RowGone, DbConflicts.Classify(new DbUpdateConcurrencyException("0 rows"))?.Kind);

        [Theory]
        [InlineData(PostgresErrorCodes.NotNullViolation)]
        [InlineData(PostgresErrorCodes.AdminShutdown)]
        [InlineData(PostgresErrorCodes.UndefinedTable)]
        public void Other_database_errors_are_not_races(string sqlState)
        {
            Assert.Null(DbConflicts.Classify(new DbUpdateException("save failed", Pg(sqlState))));
            Assert.Null(DbConflicts.Classify(Pg(sqlState)));
        }

        [Fact]
        public void Failures_without_a_database_error_are_not_races()
        {
            Assert.Null(DbConflicts.Classify(new DbUpdateException("down")));
            Assert.Null(DbConflicts.Classify(new TimeoutException()));
        }

        /// <summary>A broken or unreachable connection says nothing about the work; a stage doesn't count it against the row.</summary>
        [Fact]
        public void A_broken_connection_is_a_connection_failure()
        {
            Assert.True(DbConflicts.IsConnectionFailure(new NpgsqlException("Exception while writing to stream", new IOException("reset"))));
            Assert.True(DbConflicts.IsConnectionFailure(new DbUpdateException("save failed", new NpgsqlException("Failed to connect to 127.0.0.1:5432", new System.Net.Sockets.SocketException()))));
            // Opening timed out, or no connection was free in the pool: a timeout, but not the work's.
            Assert.True(DbConflicts.IsConnectionFailure(new NpgsqlException("Failed to connect to 127.0.0.1:5432", new TimeoutException())));
            Assert.True(DbConflicts.IsConnectionFailure(new NpgsqlException("The connection pool has been exhausted, either raise 'Max Pool Size'", new TimeoutException())));
            // The server is going away or full.
            Assert.True(DbConflicts.IsConnectionFailure(Pg(PostgresErrorCodes.AdminShutdown)));
            Assert.True(DbConflicts.IsConnectionFailure(Pg(PostgresErrorCodes.TooManyConnections)));
            Assert.True(DbConflicts.IsConnectionFailure(Pg(PostgresErrorCodes.ConnectionFailure)));
        }

        /// <summary>An I/O error from the work itself (a secret file, an HTTP call) counts against the row.</summary>
        [Fact]
        public void An_io_error_outside_the_database_is_not_a_connection_failure()
        {
            Assert.False(DbConflicts.IsConnectionFailure(new IOException("the secret file is locked")));
            Assert.False(DbConflicts.IsConnectionFailure(new HttpRequestException("refused", new System.Net.Sockets.SocketException())));
        }

        /// <summary>
        /// Npgsql calls a deadlock, a serialization failure and a command timeout transient, but each is the work's own: a row that keeps
        /// deadlocking or timing out must count toward being given up, not retry forever.
        /// </summary>
        [Fact]
        public void A_server_answer_or_a_timeout_is_not_a_connection_failure()
        {
            Assert.True(Pg(PostgresErrorCodes.DeadlockDetected).IsTransient); // what the stage must not go by
            Assert.False(DbConflicts.IsConnectionFailure(new DbUpdateException("save failed", Pg(PostgresErrorCodes.DeadlockDetected))));
            Assert.False(DbConflicts.IsConnectionFailure(Pg(PostgresErrorCodes.SerializationFailure)));
            Assert.False(DbConflicts.IsConnectionFailure(new NpgsqlException("Exception while reading from stream", new TimeoutException())));
            Assert.False(DbConflicts.IsConnectionFailure(new InvalidOperationException("a row that won't load")));
        }
    }
}
