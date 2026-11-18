using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Configuration
{
    /// <summary>A tenant's settings as the API shows them: null fields take the deployment's defaults (shown beside them).</summary>
    public sealed record TenantSettingsView(int? StormThreshold, int DefaultStormThreshold, DateTimeOffset? UpdatedAt);

    /// <summary>Sets a tenant's settings (phase-4-plan §4): the storm threshold (I43); a null field returns to the deployment's default.</summary>
    public sealed record PutTenantSettings(Guid NotificationTenantId, int? StormThreshold) : MessageBase, IRequest<Outcome<TenantSettingsView>>;

    public sealed class TenantSettingsReader(NotificationsDbContext db, Microsoft.Extensions.Options.IOptions<Storms.StormOptions> storms)
    {
        public async Task<TenantSettingsView> GetAsync(Guid tenantId, CancellationToken ct)
        {
            var row = await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId, ct);
            return new TenantSettingsView(row?.StormThreshold, storms.Value.Threshold, row?.UpdatedAt);
        }

        /// <summary>The tenant's storms, newest first: those still going, and with <paramref name="all"/> the ended ones of the last 30 days.</summary>
        public async Task<IReadOnlyList<Storms.StormView>> StormsAsync(Guid tenantId, bool all, CancellationToken ct)
        {
            var since = DateTimeOffset.UtcNow.AddDays(-30);
            return await db.Storms.AsNoTracking().Where(s => s.TenantId == tenantId && (s.EndedAt == null || all && s.StartedAt >= since))
                .OrderByDescending(s => s.StartedAt).Take(200)
                .Select(s => new Storms.StormView(s.Id, s.RouteKey, s.StartedAt, s.EndedAt, s.Peak, s.Digested)).ToListAsync(ct);
        }
    }

    internal sealed class TenantSettingsHandler(NotificationsDbContext db, TimeProvider clock, Microsoft.Extensions.Options.IOptions<Storms.StormOptions> storms)
        : IRequestHandler<PutTenantSettings, Outcome<TenantSettingsView>>
    {
        public const int MaxStormThreshold = 100_000;

        public async ValueTask<Outcome<TenantSettingsView>> Handle(PutTenantSettings r, CancellationToken ct)
        {
            if (r.StormThreshold is < 0 or > MaxStormThreshold)
            {
                return Outcome<TenantSettingsView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/stormThreshold", $"Notifications a route takes in a minute before it storms, 0 (never) to {MaxStormThreshold}, or null for the default.")]);
            }
            var row = await db.TenantSettings.SingleOrDefaultAsync(s => s.TenantId == r.NotificationTenantId, ct);
            if (row is null)
            {
                row = new TenantSettings { TenantId = r.NotificationTenantId };
                db.TenantSettings.Add(row);
            }
            row.StormThreshold = r.StormThreshold;
            row.UpdatedAt = clock.UtcNow();
            return Outcome<TenantSettingsView>.Ok(new TenantSettingsView(row.StormThreshold, storms.Value.Threshold, row.UpdatedAt));
        }
    }
}
