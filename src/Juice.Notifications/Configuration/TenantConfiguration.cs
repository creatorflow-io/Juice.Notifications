using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Configuration
{
    // A tenant's delivery setup (design §6.3, §9, §13.1): integrations (a provider connection for one channel), their targets (shared
    // destinations), routes (which notifications reach which targets) and the outbound allowlist. Rows provisioned from configuration
    // (ManagedBy = config) are refused here (phase-1-plan I12).

    public sealed record IntegrationView(string Key, string Channel, string Kind, JsonElement Settings, string? SecretRef, int Priority, bool Enabled,
        string ManagedBy, IReadOnlyList<TargetView> Targets, DateTimeOffset UpdatedAt);

    public sealed record TargetView(string Key, string? Address, string? AddressRef, bool Enabled, string ManagedBy, DateTimeOffset UpdatedAt);

    public sealed record RouteView(string Key, JsonElement Match, IReadOnlyList<string> Targets, bool Enabled, string ManagedBy, DateTimeOffset UpdatedAt);

    public sealed record AllowlistView(string Destination, IReadOnlyList<int> Ports, string ManagedBy, DateTimeOffset UpdatedAt);

    /// <summary>Creates or replaces an integration. Its channel is fixed once created.</summary>
    public sealed record PutIntegration(Guid NotificationTenantId, string Key, string? Channel, string? Kind, JsonElement? Settings, string? SecretRef,
        int? Priority, bool? Enabled) : MessageBase, IRequest<Outcome<IntegrationView>>;

    public sealed record DeleteIntegration(Guid NotificationTenantId, string Key) : MessageBase, IRequest<Outcome<bool>>;

    public sealed record PutTarget(Guid NotificationTenantId, string IntegrationKey, string Key, string? Address, string? AddressRef, bool? Enabled)
        : MessageBase, IRequest<Outcome<TargetView>>;

    public sealed record DeleteTarget(Guid NotificationTenantId, string IntegrationKey, string Key) : MessageBase, IRequest<Outcome<bool>>;

    public sealed record PutRoute(Guid NotificationTenantId, string Key, JsonElement? Match, IReadOnlyList<string>? Targets, bool? Enabled)
        : MessageBase, IRequest<Outcome<RouteView>>;

    public sealed record DeleteRoute(Guid NotificationTenantId, string Key) : MessageBase, IRequest<Outcome<bool>>;

    public sealed record PutAllowlistEntry(Guid NotificationTenantId, string Destination, IReadOnlyList<int>? Ports) : MessageBase, IRequest<Outcome<AllowlistView>>;

    public sealed record DeleteAllowlistEntry(Guid NotificationTenantId, string Destination) : MessageBase, IRequest<Outcome<bool>>;

    public static partial class ConfigurationRules
    {
        [GeneratedRegex(@"^[a-z][a-z0-9-]{0,62}\z")]
        public static partial Regex Key();

        /// <summary>
        /// A secret is named, never stored (design D15), and through the API only within the deployment's limits: an environment variable with
        /// its prefix, or a file in its folder (<see cref="Providers.SecretReferences.IsAllowedFromApi"/>).
        /// </summary>
        public static bool IsSecretRef(string? value, Providers.DeliveryOptions options)
            => value is null || value.Length <= 500 && Providers.SecretReferences.IsAllowedFromApi(value, options);

        /// <summary>A host name or an IPv4/IPv6 network in CIDR form.</summary>
        public static bool IsDestination(string value)
        {
            var slash = value.IndexOf('/');
            if (slash > 0)
            {
                return IPAddress.TryParse(value[..slash], out var network) && int.TryParse(value[(slash + 1)..], out var bits) && bits >= 0
                    && bits <= (network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            }
            return IPAddress.TryParse(value, out _) || Uri.CheckHostName(value) == UriHostNameType.Dns && value.Length <= 253;
        }

        /// <summary>A target's address, normalized for its channel, or null when it isn't one.</summary>
        public static string? Address(string channel, string address) => channel switch
        {
            Channels.Email => RequestRules.NormalizeAddress(BuiltInChannels.Email, address),
            Channels.Webhook => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo)
                && address.Length <= 2000 ? uri.AbsoluteUri : null,
            _ => null,
        };
    }

    internal sealed class ConfigurationHandlers(NotificationsDbContext db, IChannelRegistry channels, IEnumerable<INotificationProvider> providers,
        Microsoft.Extensions.Options.IOptions<Providers.DeliveryOptions> delivery, TimeProvider clock) :
        IRequestHandler<PutIntegration, Outcome<IntegrationView>>, IRequestHandler<DeleteIntegration, Outcome<bool>>,
        IRequestHandler<PutTarget, Outcome<TargetView>>, IRequestHandler<DeleteTarget, Outcome<bool>>,
        IRequestHandler<PutRoute, Outcome<RouteView>>, IRequestHandler<DeleteRoute, Outcome<bool>>,
        IRequestHandler<PutAllowlistEntry, Outcome<AllowlistView>>, IRequestHandler<DeleteAllowlistEntry, Outcome<bool>>
    {
        private static Outcome<T> Invalid<T>(params FieldError[] errors) => Outcome<T>.Invalid(ErrorCodes.InvalidConfiguration, "The configuration has errors.", errors);

        private static Outcome<T> ConfigManaged<T>(string what)
            => Outcome<T>.Conflict(ErrorCodes.ManagedByConfiguration, $"The {what} comes from the deployment's configuration; change it there.");

        public async ValueTask<Outcome<IntegrationView>> Handle(PutIntegration r, CancellationToken ct)
        {
            if (!ConfigurationRules.Key().IsMatch(r.Key))
            {
                return Invalid<IntegrationView>(new FieldError("/key", "A key is 1–63 lowercase letters, digits or hyphens, starting with a letter."));
            }
            var existing = await db.Integrations.SingleOrDefaultAsync(i => i.TenantId == r.NotificationTenantId && i.Key == r.Key, ct);
            if (existing?.ManagedBy == ManagedBy.Configuration)
            {
                return ConfigManaged<IntegrationView>("integration " + r.Key);
            }
            var channel = r.Channel ?? existing?.Channel;
            var errors = new List<FieldError>();
            if (channel is null || channels.Find(channel) is null)
            {
                errors.Add(new FieldError("/channel", "The channel is one of the registered channels."));
            }
            else if (existing is not null && existing.Channel != channel)
            {
                errors.Add(new FieldError("/channel", $"An integration's channel is fixed: {r.Key} is {existing.Channel}. Create another integration."));
            }
            var kind = r.Kind ?? existing?.Kind;
            var provider = providers.FirstOrDefault(p => p.Channel == channel && p.Kind == kind);
            if (channel is not null && provider is null)
            {
                var kinds = providers.Where(p => p.Channel == channel).Select(p => p.Kind).Order().ToList();
                errors.Add(new FieldError("/kind", kinds.Count > 0 ? $"The {channel} channel's providers are: {string.Join(", ", kinds)}." : $"No provider sends {channel}."));
            }
            if (r.Settings is { } settings && settings.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new FieldError("/settings", "Settings are a JSON object."));
            }
            if (!ConfigurationRules.IsSecretRef(r.SecretRef, delivery.Value))
            {
                errors.Add(new FieldError("/secretRef", $"A secret is named, never sent: env:{delivery.Value.ApiSecretEnvPrefix}NAME, or file: in the deployment's secret folder."));
            }
            if (provider is not null && errors.Count == 0 && provider.CheckSettings(r.Settings is { } given ? given.GetRawText() : existing?.Settings ?? "{}") is { } settingsProblem)
            {
                errors.Add(new FieldError("/settings", settingsProblem));
            }
            if (errors.Count > 0)
            {
                return Invalid<IntegrationView>([.. errors]);
            }
            var now = clock.UtcNow();
            var settingsJson = r.Settings is { } s ? CanonicalJson.Write(s) : existing?.Settings ?? "{}";
            if (existing is null)
            {
                existing = new Integration
                {
                    TenantId = r.NotificationTenantId, Key = r.Key, Channel = channel!, Kind = kind!, Settings = settingsJson, SecretRef = r.SecretRef,
                    Priority = r.Priority ?? 0, Enabled = r.Enabled ?? true, DefinitionHash = "", UpdatedAt = now,
                };
                db.Integrations.Add(existing);
            }
            else
            {
                existing.Kind = kind!;
                existing.Settings = settingsJson;
                existing.SecretRef = r.SecretRef ?? existing.SecretRef;
                existing.Priority = r.Priority ?? existing.Priority;
                existing.Enabled = r.Enabled ?? existing.Enabled;
                existing.UpdatedAt = now;
            }
            existing.DefinitionHash = CanonicalJson.Sha256($"{existing.Channel}|{existing.Kind}|{existing.Settings}|{existing.SecretRef}|{existing.Priority}|{existing.Enabled}");
            return Outcome<IntegrationView>.Ok(await ConfigurationQueries.IntegrationAsync(db, existing, ct));
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteIntegration r, CancellationToken ct)
        {
            var existing = await db.Integrations.SingleOrDefaultAsync(i => i.TenantId == r.NotificationTenantId && i.Key == r.Key, ct);
            if (existing is null)
            {
                return Outcome<bool>.Ok(false);
            }
            if (existing.ManagedBy == ManagedBy.Configuration)
            {
                return ConfigManaged<bool>("integration " + r.Key);
            }
            if (await db.Deliveries.AnyAsync(d => d.TenantId == r.NotificationTenantId && d.IntegrationId == existing.Id, ct))
            {
                return Outcome<bool>.Conflict(ErrorCodes.IntegrationInUse, "Deliveries went through this integration; disable it instead, so their history keeps it.");
            }
            db.Integrations.Remove(existing);
            return Outcome<bool>.Ok(true);
        }

        public async ValueTask<Outcome<TargetView>> Handle(PutTarget r, CancellationToken ct)
        {
            if (!ConfigurationRules.Key().IsMatch(r.Key))
            {
                return Invalid<TargetView>(new FieldError("/key", "A key is 1–63 lowercase letters, digits or hyphens, starting with a letter."));
            }
            var integration = await db.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == r.NotificationTenantId && i.Key == r.IntegrationKey, ct);
            if (integration is null)
            {
                return Outcome<TargetView>.NotFound($"There is no integration '{r.IntegrationKey}'.");
            }
            var existing = await db.IntegrationTargets.SingleOrDefaultAsync(t => t.TenantId == r.NotificationTenantId && t.Key == r.Key, ct);
            if (existing is not null && existing.IntegrationId != integration.Id)
            {
                return Outcome<TargetView>.Conflict(ErrorCodes.TargetExists, $"The target key '{r.Key}' is taken by another integration; target keys are unique in a tenant.");
            }
            if (existing?.ManagedBy == ManagedBy.Configuration)
            {
                return ConfigManaged<TargetView>("target " + r.Key);
            }
            string? address = null;
            if (r.Address is not null && (address = ConfigurationRules.Address(integration.Channel, r.Address)) is null)
            {
                return Invalid<TargetView>(new FieldError("/address", integration.Channel == Channels.Email
                    ? "The address is a bare email address." : "The address is an absolute http or https URL without credentials."));
            }
            if (!ConfigurationRules.IsSecretRef(r.AddressRef, delivery.Value))
            {
                return Invalid<TargetView>(new FieldError("/addressRef", $"A secret address is named: env:{delivery.Value.ApiSecretEnvPrefix}NAME, or file: in the deployment's secret folder."));
            }
            if ((address ?? existing?.Address) is null && (r.AddressRef ?? existing?.AddressRef) is null)
            {
                return Invalid<TargetView>(new FieldError("/address", "A target has an address, or an addressRef naming where its secret address is."));
            }
            var now = clock.UtcNow();
            if (existing is null)
            {
                existing = new IntegrationTarget
                {
                    TenantId = r.NotificationTenantId, IntegrationId = integration.Id, Key = r.Key, Address = address, AddressRef = r.AddressRef,
                    Enabled = r.Enabled ?? true, DefinitionHash = "", UpdatedAt = now,
                };
                db.IntegrationTargets.Add(existing);
            }
            else
            {
                if (address is not null || r.AddressRef is not null)
                {
                    existing.Address = address;
                    existing.AddressRef = r.AddressRef;
                }
                existing.Enabled = r.Enabled ?? existing.Enabled;
                existing.UpdatedAt = now;
            }
            existing.DefinitionHash = CanonicalJson.Sha256($"{existing.Address}|{existing.AddressRef}|{existing.Enabled}");
            return Outcome<TargetView>.Ok(ConfigurationQueries.Target(existing));
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteTarget r, CancellationToken ct)
        {
            var integration = await db.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == r.NotificationTenantId && i.Key == r.IntegrationKey, ct);
            var existing = integration is null ? null
                : await db.IntegrationTargets.SingleOrDefaultAsync(t => t.TenantId == r.NotificationTenantId && t.IntegrationId == integration.Id && t.Key == r.Key, ct);
            if (existing is null)
            {
                return Outcome<bool>.Ok(false);
            }
            if (existing.ManagedBy == ManagedBy.Configuration)
            {
                return ConfigManaged<bool>("target " + r.Key);
            }
            db.IntegrationTargets.Remove(existing);
            return Outcome<bool>.Ok(true);
        }

        public async ValueTask<Outcome<RouteView>> Handle(PutRoute r, CancellationToken ct)
        {
            if (!ConfigurationRules.Key().IsMatch(r.Key))
            {
                return Invalid<RouteView>(new FieldError("/key", "A key is 1–63 lowercase letters, digits or hyphens, starting with a letter."));
            }
            var existing = await db.Routes.SingleOrDefaultAsync(x => x.TenantId == r.NotificationTenantId && x.Key == r.Key, ct);
            if (existing?.ManagedBy == ManagedBy.Configuration)
            {
                return ConfigManaged<RouteView>("route " + r.Key);
            }
            var errors = new List<FieldError>();
            string? match = null;
            if (r.Match is { } m)
            {
                var (parsed, matchErrors) = RouteMatch.Parse(m);
                errors.AddRange(matchErrors);
                match = parsed is null ? null : CanonicalJson.Write(m);
            }
            else if (existing is null)
            {
                errors.Add(new FieldError("/match", "A route needs a match; {} matches every notification."));
            }
            var targets = r.Targets?.Distinct(StringComparer.Ordinal).ToArray();
            if (targets is not null)
            {
                if (targets.Length is 0 or > 50)
                {
                    errors.Add(new FieldError("/targets", "A route has 1–50 targets."));
                }
                var known = await db.IntegrationTargets.Where(t => t.TenantId == r.NotificationTenantId && targets.Contains(t.Key)).Select(t => t.Key).ToListAsync(ct);
                for (var i = 0; i < r.Targets!.Count; i++)
                {
                    if (!known.Contains(r.Targets[i]))
                    {
                        errors.Add(new FieldError($"/targets/{i}", $"There is no target '{r.Targets[i]}' in this tenant."));
                    }
                }
            }
            else if (existing is null)
            {
                errors.Add(new FieldError("/targets", "A route has 1–50 targets."));
            }
            if (errors.Count > 0)
            {
                return Invalid<RouteView>([.. errors]);
            }
            var now = clock.UtcNow();
            if (existing is null)
            {
                existing = new Route { TenantId = r.NotificationTenantId, Key = r.Key, Match = match!, Targets = targets!, Enabled = r.Enabled ?? true, DefinitionHash = "", UpdatedAt = now };
                db.Routes.Add(existing);
            }
            else
            {
                existing.Match = match ?? existing.Match;
                existing.Targets = targets ?? existing.Targets;
                existing.Enabled = r.Enabled ?? existing.Enabled;
                existing.UpdatedAt = now;
            }
            existing.DefinitionHash = CanonicalJson.Sha256($"{existing.Match}|{string.Join(",", existing.Targets)}|{existing.Enabled}");
            return Outcome<RouteView>.Ok(ConfigurationQueries.Route(existing));
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteRoute r, CancellationToken ct)
        {
            var existing = await db.Routes.SingleOrDefaultAsync(x => x.TenantId == r.NotificationTenantId && x.Key == r.Key, ct);
            if (existing is null)
            {
                return Outcome<bool>.Ok(false);
            }
            if (existing.ManagedBy == ManagedBy.Configuration)
            {
                return ConfigManaged<bool>("route " + r.Key);
            }
            db.Routes.Remove(existing);
            return Outcome<bool>.Ok(true);
        }

        public async ValueTask<Outcome<AllowlistView>> Handle(PutAllowlistEntry r, CancellationToken ct)
        {
            var destination = r.Destination.Trim().ToLowerInvariant();
            if (!ConfigurationRules.IsDestination(destination))
            {
                return Invalid<AllowlistView>(new FieldError("/destination", "A destination is a host name, an IP address, or a network in CIDR form (10.20.0.0/16)."));
            }
            var ports = r.Ports?.Distinct().Order().ToArray() ?? [];
            if (ports.Any(p => p is < 1 or > 65535) || ports.Length > 20)
            {
                return Invalid<AllowlistView>(new FieldError("/ports", "Up to 20 ports, each 1–65535; none means 443 only."));
            }
            var existing = await db.Allowlist.SingleOrDefaultAsync(a => a.TenantId == r.NotificationTenantId && a.Destination == destination, ct);
            if (existing?.ManagedBy == ManagedBy.Configuration)
            {
                return ConfigManaged<AllowlistView>("allowlist entry " + destination);
            }
            if (existing is null)
            {
                existing = new AllowlistEntry { TenantId = r.NotificationTenantId, Destination = destination };
                db.Allowlist.Add(existing);
            }
            existing.Ports = ports;
            existing.UpdatedAt = clock.UtcNow();
            return Outcome<AllowlistView>.Ok(ConfigurationQueries.Allowlist(existing));
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteAllowlistEntry r, CancellationToken ct)
        {
            var destination = r.Destination.Trim().ToLowerInvariant();
            var existing = await db.Allowlist.SingleOrDefaultAsync(a => a.TenantId == r.NotificationTenantId && a.Destination == destination, ct);
            if (existing is null)
            {
                return Outcome<bool>.Ok(false);
            }
            if (existing.ManagedBy == ManagedBy.Configuration)
            {
                return ConfigManaged<bool>("allowlist entry " + destination);
            }
            db.Allowlist.Remove(existing);
            return Outcome<bool>.Ok(true);
        }
    }

    public static class ConfigurationQueries
    {
        internal static async Task<IntegrationView> IntegrationAsync(NotificationsDbContext db, Integration i, CancellationToken ct)
        {
            var targets = await db.IntegrationTargets.AsNoTracking().Where(t => t.TenantId == i.TenantId && t.IntegrationId == i.Id).OrderBy(t => t.Key).ToListAsync(ct);
            return View(i, targets);
        }

        internal static IntegrationView View(Integration i, IEnumerable<IntegrationTarget> targets)
            => new(i.Key, i.Channel, i.Kind, JsonElement.Parse(i.Settings), i.SecretRef, i.Priority, i.Enabled, i.ManagedBy, targets.Select(Target).ToList(), i.UpdatedAt);

        internal static TargetView Target(IntegrationTarget t) => new(t.Key, t.Address, t.AddressRef, t.Enabled, t.ManagedBy, t.UpdatedAt);

        internal static RouteView Route(Route r) => new(r.Key, JsonElement.Parse(r.Match), r.Targets, r.Enabled, r.ManagedBy, r.UpdatedAt);

        internal static AllowlistView Allowlist(AllowlistEntry a) => new(a.Destination, a.Ports, a.ManagedBy, a.UpdatedAt);
    }

    /// <summary>Reads a tenant's configuration, outside any command.</summary>
    public sealed class ConfigurationReader(NotificationsDbContext db)
    {
        public async Task<IReadOnlyList<IntegrationView>> IntegrationsAsync(Guid tenantId, CancellationToken ct)
        {
            var integrations = await db.Integrations.AsNoTracking().Where(i => i.TenantId == tenantId).OrderBy(i => i.Key).ToListAsync(ct);
            var targets = (await db.IntegrationTargets.AsNoTracking().Where(t => t.TenantId == tenantId).OrderBy(t => t.Key).ToListAsync(ct)).ToLookup(t => t.IntegrationId);
            return integrations.Select(i => ConfigurationQueries.View(i, targets[i.Id])).ToList();
        }

        public async Task<IReadOnlyList<RouteView>> RoutesAsync(Guid tenantId, CancellationToken ct)
            => (await db.Routes.AsNoTracking().Where(r => r.TenantId == tenantId).OrderBy(r => r.Key).ToListAsync(ct)).Select(ConfigurationQueries.Route).ToList();

        public async Task<IReadOnlyList<AllowlistView>> AllowlistAsync(Guid tenantId, CancellationToken ct)
            => (await db.Allowlist.AsNoTracking().Where(a => a.TenantId == tenantId).OrderBy(a => a.Destination).ToListAsync(ct)).Select(ConfigurationQueries.Allowlist).ToList();
    }
}
