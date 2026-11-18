using System.Text.Json;
using Juice.MediatR;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Storms
{
    /// <summary>Configuration section <c>Storm</c> (design §14, phase-4-plan I43).</summary>
    public sealed class StormOptions
    {
        public const string Section = "Storm";

        /// <summary>Notifications a route takes in a minute before it storms; a tenant's setting overrides it. 0 turns storm protection off.</summary>
        public int Threshold { get; set; } = 100;

        /// <summary>How long a route stays under half the threshold before its storm ends.</summary>
        public TimeSpan CalmFor { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>The digest window a stormy route's recipients join.</summary>
        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>The role told when a storm starts (a role recipient, resolved by the tenant's resolvers): the tenant's notification admins.</summary>
        public string NotifyRole { get; set; } = "notifications-admin";
    }

    public sealed record StormView(Guid Id, string Route, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int Peak, int Digested);

    /// <summary>
    /// Storm protection (design §14, phase-4-plan I43). Each notification sent to a route counts in that route's minute; a route past the threshold
    /// storms: its recipients join a one-minute digest (the digest machinery of phase 3, by route) instead of a delivery each, so a storm of a
    /// thousand notifications reaches each target as a digest a minute and none is dropped. The storm ends once the route stayed under half the
    /// threshold for <see cref="StormOptions.CalmFor"/> (checked as notifications arrive). The role <see cref="StormOptions.NotifyRole"/> is told
    /// once per storm, by the service's type <c>notifications.storm</c>.
    /// </summary>
    public sealed class StormGuard(IOptions<StormOptions> options, IServiceScopeFactory scopes, ILogger<StormGuard> logger)
    {
        public TimeSpan Window => options.Value.Window;

        /// <summary>Whether any of the routes has a storm going (one that may have calmed: the next count ends it).</summary>
        public Task<bool> AnyOpenAsync(NotificationsDbContext db, Guid tenantId, IEnumerable<string> routes, CancellationToken ct)
        {
            var keys = routes.ToList();
            return db.Storms.AsNoTracking().AnyAsync(s => s.TenantId == tenantId && keys.Contains(s.RouteKey) && s.EndedAt == null, ct);
        }

        /// <summary>
        /// Counts one notification's recipients on each route (<paramref name="routes"/>: route key and how many of its recipients) and says which
        /// routes are storming now. Each count commits at once, outside the step: a step run again counts twice, which only errs towards a storm.
        /// </summary>
        public async Task<HashSet<string>> CountAsync(NotificationsDbContext db, Guid tenantId, IReadOnlyDictionary<string, int> routes, DateTimeOffset now,
            List<Action> afterCommit, CancellationToken ct)
        {
            var storming = new HashSet<string>(StringComparer.Ordinal);
            if (routes.Count == 0)
            {
                return storming;
            }
            var threshold = await db.TenantSettings.AsNoTracking().Where(s => s.TenantId == tenantId).Select(s => s.StormThreshold).SingleOrDefaultAsync(ct)
                ?? options.Value.Threshold;
            if (threshold <= 0)
            {
                return storming;
            }
            var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
            foreach (var (route, n) in routes)
            {
                // Not composed (no SingleAsync): an INSERT … RETURNING can't sit in the subquery EF would wrap it in.
                var count = (await db.Database.SqlQuery<int>($"""
                    INSERT INTO notifications.route_minutes AS m (tenant_id, route_key, minute, count) VALUES ({tenantId}, {route}, {minute}, {n})
                    ON CONFLICT (tenant_id, route_key, minute) DO UPDATE SET count = m.count + EXCLUDED.count
                    RETURNING count AS "Value"
                    """).ToListAsync(ct)).Single();
                if (count == n)
                {
                    // A new minute for the route: the hour-old ones go (the counts only matter for minutes).
                    await db.RouteMinutes.Where(m => m.TenantId == tenantId && m.RouteKey == route && m.Minute < now.AddHours(-1)).ExecuteDeleteAsync(ct);
                }
                var storm = await db.Storms.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId && s.RouteKey == route && s.EndedAt == null, ct);
                if (storm is not null)
                {
                    if (count * 2 >= threshold)
                    {
                        await db.Storms.Where(s => s.TenantId == tenantId && s.Id == storm.Id)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastHighAt, now).SetProperty(x => x.Peak, x => x.Peak > count ? x.Peak : count)
                                .SetProperty(x => x.Digested, x => x.Digested + n), ct);
                        storming.Add(route);
                    }
                    else if (now - storm.LastHighAt >= options.Value.CalmFor)
                    {
                        await db.Storms.Where(s => s.TenantId == tenantId && s.Id == storm.Id && s.EndedAt == null)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndedAt, now), ct);
                        logger.LogInformation("The storm on route {Route} in tenant {Tenant} ended: {Digested} recipients went to digests, at most {Peak} a minute.",
                            route, tenantId, storm.Digested, storm.Peak);
                    }
                    else
                    {
                        await db.Storms.Where(s => s.TenantId == tenantId && s.Id == storm.Id)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Digested, x => x.Digested + n), ct);
                        storming.Add(route);
                    }
                    continue;
                }
                if (count <= threshold)
                {
                    continue;
                }
                var id = Guid.CreateVersion7();
                var started = await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO notifications.storms (tenant_id, id, route_key, started_at, last_high_at, peak, digested)
                    VALUES ({tenantId}, {id}, {route}, {now}, {now}, {count}, {n})
                    ON CONFLICT (tenant_id, route_key) WHERE ended_at IS NULL DO NOTHING
                    """, ct);
                storming.Add(route);
                if (started > 0)
                {
                    logger.LogWarning("Route {Route} in tenant {Tenant} is storming: {Count} notifications this minute, over {Threshold}. It sends digests until it calms.",
                        route, tenantId, count, threshold);
                    Telemetry.Storms.Add(1);
                    afterCommit.Add(() => _ = NotifyAsync(tenantId, route, threshold, count));
                }
            }
            return storming;
        }

        /// <summary>Tells the tenant's notification admins, once per storm (the service's own type, in their inbox). A failure is logged, never thrown.</summary>
        private async Task NotifyAsync(Guid tenantId, string route, int threshold, int count)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var app = await db.Applications.AsNoTracking().Where(a => a.Key == People.ServiceTypes.ApplicationKey).Select(a => new { a.Id, a.Enabled }).SingleAsync();
                var now = DateTimeOffset.UtcNow;
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO notifications.application_tenants (application_id, tenant_id, linked_at) VALUES ({app.Id}, {tenantId}, {now})
                    ON CONFLICT DO NOTHING
                    """);
                var submission = new NotificationSubmission(People.ServiceTypes.StormType, null, $"storm:{route}:{now.UtcTicks}", null,
                    [new RecipientSpec(RecipientKinds.Role, Id: options.Value.NotifyRole)], new ActorSpec("system", "storm-protection"), null, null,
                    JsonSerializer.SerializeToElement(new { route, threshold, count }), null, null);
                var accepted = await mediator.Send(new AcceptNotification(tenantId, app.Id, People.ServiceTypes.ApplicationKey, submission, null, app.Enabled));
                if (accepted.Value is null)
                {
                    logger.LogWarning("The storm notice for route {Route} in tenant {Tenant} wasn't accepted: {Reason}", route, tenantId, accepted.Message);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The storm notice for route {Route} in tenant {Tenant} couldn't be sent.", route, tenantId);
            }
        }
    }
}
