using Juice.MediatR;
using Juice.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Configuration
{
    /// <summary>
    /// Hands the alias's configuration rows for one endpoint back to the alias's API (alias-config -> webhooks-alias), disabled, as
    /// Provisioning hands config -> api (phase-5 M46c). In-process only (there is no HTTP route); <see cref="ManagedBy"/> must be
    /// <see cref="Notifications.ManagedBy.AliasConfig"/>, any other caller is refused with <see cref="ErrorCodes.ManagedByConfiguration"/>.
    /// Not an <c>Owned()</c> rule: letting the alias's API write alias-config rows would let it edit what the file declares.
    /// </summary>
    public sealed record HandBackAliasConfig(Guid NotificationTenantId, string IntegrationKey)
        : MessageBase, ITenantCommand, IRequest<Outcome<HandBackView>>, IOwnedSetupWrite
    {
        public string? ManagedBy { get; init; }
    }

    /// <summary>The rows a hand-back moved.</summary>
    public sealed record HandBackView(string IntegrationKey, int Integrations, int Targets, int Routes);

    internal sealed partial class ConfigurationHandlers : IRequestHandler<HandBackAliasConfig, Outcome<HandBackView>>
    {
        /// <summary>
        /// Moves the tenant's alias-config integration <c>IntegrationKey</c>, its targets and the route of the same key to the alias's API,
        /// disabled. Nothing is saved here: the caller's one SaveChanges writes all rows or none, and a concurrent owner change (ManagedBy is a
        /// concurrency token) fails that save, so ownership is never half moved. A row owned by anyone else, already handed back, or missing is
        /// left as it is.
        /// </summary>
        public async ValueTask<Outcome<HandBackView>> Handle(HandBackAliasConfig r, CancellationToken ct)
        {
            if (r.ManagedBy != ManagedBy.AliasConfig)
            {
                return Outcome<HandBackView>.Conflict(ErrorCodes.ManagedByConfiguration,
                    "Only the alias's configuration hands its rows back to the alias's API.");
            }
            var now = clock.UtcNow();
            var (integrations, targets, routes) = (0, 0, 0);

            var integration = await db.Integrations.SingleOrDefaultAsync(i => i.TenantId == r.NotificationTenantId && i.Key == r.IntegrationKey, ct);
            if (integration is not null)
            {
                if (integration.ManagedBy == ManagedBy.AliasConfig)
                {
                    integration.ManagedBy = ManagedBy.WebhooksAlias;
                    integration.Enabled = false;
                    integration.UpdatedAt = now;
                    integration.DefinitionHash = ConfigurationRules.IntegrationHash(integration);
                    integrations++;
                }
                foreach (var target in await db.IntegrationTargets
                    .Where(t => t.TenantId == r.NotificationTenantId && t.IntegrationId == integration.Id && t.ManagedBy == ManagedBy.AliasConfig).ToListAsync(ct))
                {
                    target.ManagedBy = ManagedBy.WebhooksAlias;
                    target.Enabled = false;
                    target.UpdatedAt = now;
                    target.DefinitionHash = ConfigurationRules.TargetHash(target);
                    targets++;
                }
            }
            var route = await db.Routes.SingleOrDefaultAsync(x => x.TenantId == r.NotificationTenantId && x.Key == r.IntegrationKey, ct);
            if (route is { ManagedBy: ManagedBy.AliasConfig })
            {
                route.ManagedBy = ManagedBy.WebhooksAlias;
                route.Enabled = false;
                route.UpdatedAt = now;
                route.DefinitionHash = ConfigurationRules.RouteHash(route);
                routes++;
            }
            return Outcome<HandBackView>.Ok(new HandBackView(r.IntegrationKey, integrations, targets, routes));
        }
    }
}
