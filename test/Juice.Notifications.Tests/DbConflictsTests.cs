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
    }
}
