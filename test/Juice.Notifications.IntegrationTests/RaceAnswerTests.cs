using Juice.MediatR;
using Juice.Notifications.Api;
using Juice.Notifications.Applications;
using Juice.Notifications.Data;
using Race = Juice.Notifications.Api.ApplicationEndpoints.Sender.Race;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>The race table: which answer each command gets for each conflict. Unknown conflicts are not answered. Needs no local stack.</summary>
    public sealed class RaceAnswerTests
    {
        private static readonly CreateApplication Create = new("app", "App", "client");
        private static readonly UpdateApplication Update = new("app", null, "client", null);
        private static readonly LinkTenant Link = new("app", Guid.CreateVersion7());
        private static readonly UnlinkTenant Unlink = new("app", Guid.CreateVersion7());

        public static TheoryData<string, ConflictKind, string?, string> Table => new()
        {
            { "create", ConflictKind.Unique, Constraints.ApplicationKey, nameof(Race.CreateTaken) },
            { "create", ConflictKind.Unique, Constraints.ApplicationClientId, nameof(Race.CreateTaken) },
            { "update", ConflictKind.Unique, Constraints.ApplicationClientId, nameof(Race.ClientIdTaken) },
            { "link", ConflictKind.Unique, Constraints.ApplicationTenantLink, nameof(Race.SameEndState) },
            { "unlink", ConflictKind.RowGone, null, nameof(Race.SameEndState) },
            { "unlink", ConflictKind.ForeignKey, Constraints.RequestApplicationTenant, nameof(Race.TenantInUse) },
            { "create", ConflictKind.Aborted, null, nameof(Race.TryAgain) },
            { "link", ConflictKind.Aborted, null, nameof(Race.TryAgain) },
            { "update", ConflictKind.RowGone, null, nameof(Race.TryAgain) },
            // Not races this API knows: they reach the error handler.
            { "create", ConflictKind.Unique, "ix_some_later_index", nameof(Race.Unknown) },
            { "update", ConflictKind.Unique, Constraints.ApplicationKey, nameof(Race.Unknown) },
            { "link", ConflictKind.ForeignKey, "fk_application_tenants_applications_application_id", nameof(Race.Unknown) },
            { "unlink", ConflictKind.ForeignKey, "fk_some_other_reference", nameof(Race.Unknown) },
            { "link", ConflictKind.Unique, Constraints.ApplicationKey, nameof(Race.Unknown) },
        };

        [Theory]
        [MemberData(nameof(Table))]
        public void Each_conflict_gets_its_answer(string command, ConflictKind kind, string? constraint, string expected)
        {
            IRequest<Outcome<ApplicationView>> sent = command switch { "create" => Create, "update" => Update, "link" => Link, _ => Unlink };
            Assert.Equal(expected, ApplicationEndpoints.Sender.Answer(sent, new DbConflict(kind, constraint)).ToString());
        }
    }
}
