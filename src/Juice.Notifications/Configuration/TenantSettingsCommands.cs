using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Configuration
{
    /// <summary>A tenant's settings as the API shows them: null fields take the deployment's defaults (shown beside them).</summary>
    public sealed record TenantSettingsView(int? StormThreshold, int DefaultStormThreshold, int? RequestRetentionDays, int DefaultRequestRetentionDays,
        int? ContentRetentionDays, int DefaultContentRetentionDays, DateTimeOffset? UpdatedAt, int Version);

    /// <summary>
    /// Sets a tenant's settings (phase-4-plan §4): the storm threshold (I43); a null field returns to the deployment's default. With an
    /// <paramref name="ExpectedVersion"/> (M52), a save over another version is a conflict; without one it is written as before.
    /// </summary>
    public sealed record PutTenantSettings(Guid NotificationTenantId, int? StormThreshold, int? RequestRetentionDays = null, int? ContentRetentionDays = null,
        int? ExpectedVersion = null)
        : MessageBase, ITenantCommand, IRequest<Outcome<TenantSettingsView>>;

    internal static class TenantSettingsViews
    {
        public static TenantSettingsView View(TenantSettings? row, Storms.StormOptions storms, Retention.RetentionOptions retention)
            => new(row?.StormThreshold, storms.Threshold, row?.RequestRetentionDays, retention.RequestDays, row?.ContentRetentionDays, retention.ContentDays,
                row?.UpdatedAt, row?.Version ?? 0);
    }

    public sealed class TenantSettingsReader(NotificationsDbContext db, Microsoft.Extensions.Options.IOptions<Storms.StormOptions> storms,
        Microsoft.Extensions.Options.IOptions<Retention.RetentionOptions> retention)
    {
        public async Task<TenantSettingsView> GetAsync(Guid tenantId, CancellationToken ct)
            => TenantSettingsViews.View(await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId, ct), storms.Value, retention.Value);

        /// <summary>The tenant's storms, newest first: those still going, and with <paramref name="all"/> the ended ones of the last 30 days.</summary>
        public async Task<IReadOnlyList<Storms.StormView>> StormsAsync(Guid tenantId, bool all, CancellationToken ct)
        {
            var since = DateTimeOffset.UtcNow.AddDays(-30);
            return await db.Storms.AsNoTracking().Where(s => s.TenantId == tenantId && (s.EndedAt == null || all && s.StartedAt >= since))
                .OrderByDescending(s => s.StartedAt).Take(200)
                .Select(s => new Storms.StormView(s.Id, s.RouteKey, s.StartedAt, s.EndedAt, s.Peak, s.Digested)).ToListAsync(ct);
        }
    }

    internal sealed class TenantSettingsHandler(NotificationsDbContext db, TimeProvider clock, Microsoft.Extensions.Options.IOptions<Storms.StormOptions> storms,
        Microsoft.Extensions.Options.IOptions<Retention.RetentionOptions> retention)
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
            if (r.RequestRetentionDays is < 1 or > 3650)
            {
                return Outcome<TenantSettingsView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/requestRetentionDays", "Days requests and deliveries are kept, 1 to 3650, or null for the default.")]);
            }
            if (r.ContentRetentionDays is < 1 or > 3650 || r.ContentRetentionDays > (r.RequestRetentionDays ?? retention.Value.RequestDays))
            {
                return Outcome<TenantSettingsView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/contentRetentionDays", "Days rendered content is kept, 1 to the request retention, or null for the default.")]);
            }
            var row = await db.TenantSettings.SingleOrDefaultAsync(s => s.TenantId == r.NotificationTenantId, ct);
            if (r.ExpectedVersion is { } expected && expected != (row?.Version ?? 0))
            {
                return Outcome<TenantSettingsView>.Conflict(ErrorCodes.ConcurrentChange, "The settings changed since they were read. Read them again.");
            }
            if (row is null)
            {
                row = new TenantSettings { TenantId = r.NotificationTenantId };
                db.TenantSettings.Add(row);
            }
            row.StormThreshold = r.StormThreshold;
            row.RequestRetentionDays = r.RequestRetentionDays;
            row.ContentRetentionDays = r.ContentRetentionDays;
            row.UpdatedAt = clock.UtcNow();
            row.Version++; // the concurrency token: a save that read an older version loses (RowGone)
            return Outcome<TenantSettingsView>.Ok(TenantSettingsViews.View(row, storms.Value, retention.Value));
        }
    }
}
