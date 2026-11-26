using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The <c>RecipientDryStamp</c> migration (M44-2c) over rows already there: the request's own recipients of a request accepted dry take its
    /// flag, so they stay dry however late they are planned; those of a real request, and every escalation step's, are real.
    /// </summary>
    public sealed class RecipientDryStampMigrationTests : IAsyncLifetime
    {
        private const string Previous = "20261126090432_RequestDryStamp";
        private static readonly Guid Tenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000000c2");
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly ScratchDatabase _database = new();

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        [Fact]
        public async Task Existing_own_recipients_of_a_dry_request_become_dry_and_the_rest_stay_real()
        {
            LocalStack.RequireAvailable();
            var ids = new Dictionary<string, Guid>();
            await using (var db = await _database.MigratedAsync())
            {
                var app = Seed.Application("dry-stamp-migration");
                var type = Seed.Type(app.Id, "dry-stamp-migration.thing");
                db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
                foreach (var (name, dry, step) in new[] { ("dry-own", true, 0), ("dry-step", true, 1), ("real-own", false, 0) })
                {
                    var request = Seed.Request(Tenant, app.Id, type.Id, name);
                    db.Entry(request).Property(r => r.DryRun).CurrentValue = dry;
                    var notification = Seed.Notification(Tenant, request.Id);
                    var recipient = Seed.Recipient(Tenant, notification.Id);
                    recipient.EscalationStep = step;
                    db.AddRange(request, notification, recipient);
                    ids[name] = recipient.Id;
                }
                await db.SaveChangesAsync(Ct);
                await db.GetService<IMigrator>().MigrateAsync(Previous, cancellationToken: Ct);
            }

            await using (var db = _database.Context())
            {
                await db.Database.MigrateAsync(Ct);
                var flags = await db.Recipients.AsNoTracking().Where(r => r.TenantId == Tenant).ToDictionaryAsync(r => r.Id, r => r.DryRun, Ct);
                Assert.Equal((true, false, false), (flags[ids["dry-own"]], flags[ids["dry-step"]], flags[ids["real-own"]]));
            }
        }
    }
}
