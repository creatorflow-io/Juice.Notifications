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

    // The Put commands come from the API, where a field left out keeps its stored value, or from an in-process owner (ManagedBy): configuration
    // provisioning, where the file states the whole definition, may change a row the file owns and takes over one the API made; the webhooks alias
    // (phase 5 M46) writes through the API's rules but owns its rows. A Put that changes nothing leaves the row as it was: no new UpdatedAt, no
    // audit record.

    /// <summary>
    /// A setup write and who makes it (phase-5 decision G1). <see cref="ManagedBy"/> is set only by an in-process caller: the API builds its
    /// commands field by field and never sets it, so null is the API. Each owner changes and deletes only its own rows (configuration also takes
    /// over the API's); another owner's row is refused with <see cref="ErrorCodes.ManagedByConfiguration"/>, <see cref="ErrorCodes.ManagedByAlias"/>
    /// or <see cref="ErrorCodes.ManagedByApi"/>.
    /// </summary>
    public interface IOwnedSetupWrite
    {
        /// <summary><see cref="Notifications.ManagedBy.Configuration"/>, <see cref="Notifications.ManagedBy.WebhooksAlias"/>, or null for the API.</summary>
        string? ManagedBy { get; }
    }

    /// <summary>Creates or replaces an integration. Its channel is fixed once created.</summary>
    public sealed record PutIntegration(Guid NotificationTenantId, string Key, string? Channel, string? Kind, JsonElement? Settings, string? SecretRef,
        int? Priority, bool? Enabled) : MessageBase, ITenantCommand, IRequest<Outcome<IntegrationView>>, IOwnedSetupWrite
    {
        public string? ManagedBy { get; init; }

        internal bool Provisioned => ManagedBy == Notifications.ManagedBy.Configuration;

        /// <summary>
        /// The id a new integration takes (phase-5 M46-2): an in-process caller's, as the webhooks alias keeps a legacy endpoint's id. The API
        /// never sets it; null makes a new one. Only a create takes it: an existing integration keeps its own, and a different one is refused, as
        /// is one another integration of the tenant has.
        /// </summary>
        public Guid? Id { get; init; }
    }

    public sealed record DeleteIntegration(Guid NotificationTenantId, string Key) : MessageBase, ITenantCommand, IRequest<Outcome<bool>>, IOwnedSetupWrite
    {
        public string? ManagedBy { get; init; }
    }

    public sealed record PutTarget(Guid NotificationTenantId, string IntegrationKey, string Key, string? Address, string? AddressRef, bool? Enabled)
        : MessageBase, ITenantCommand, IRequest<Outcome<TargetView>>, IOwnedSetupWrite
    {
        public string? ManagedBy { get; init; }

        internal bool Provisioned => ManagedBy == Notifications.ManagedBy.Configuration;
    }

    public sealed record DeleteTarget(Guid NotificationTenantId, string IntegrationKey, string Key) : MessageBase, ITenantCommand, IRequest<Outcome<bool>>, IOwnedSetupWrite
    {
        public string? ManagedBy { get; init; }
    }

    public sealed record PutRoute(Guid NotificationTenantId, string Key, JsonElement? Match, IReadOnlyList<string>? Targets, bool? Enabled)
        : MessageBase, ITenantCommand, IRequest<Outcome<RouteView>>, IOwnedSetupWrite
    {
        public string? ManagedBy { get; init; }

        internal bool Provisioned => ManagedBy == Notifications.ManagedBy.Configuration;
    }

    public sealed record DeleteRoute(Guid NotificationTenantId, string Key) : MessageBase, ITenantCommand, IRequest<Outcome<bool>>, IOwnedSetupWrite
    {
        public string? ManagedBy { get; init; }
    }

    public sealed record PutAllowlistEntry(Guid NotificationTenantId, string Destination, IReadOnlyList<int>? Ports) : MessageBase, ITenantCommand, IRequest<Outcome<AllowlistView>>,
        IOwnedSetupWrite
    {
        public string? ManagedBy { get; init; }

        internal bool Provisioned => ManagedBy == Notifications.ManagedBy.Configuration;
    }

    public sealed record DeleteAllowlistEntry(Guid NotificationTenantId, string Destination) : MessageBase, ITenantCommand, IRequest<Outcome<bool>>, IOwnedSetupWrite
    {
        public string? ManagedBy { get; init; }
    }

    public static partial class ConfigurationRules
    {
        [GeneratedRegex(@"^[a-z][a-z0-9-]{0,62}\z")]
        public static partial Regex Key();

        /// <summary>
        /// A secret is named, never stored (design D15), and through the API only within the deployment's limits: an environment variable with
        /// its prefix, or a file in its folder (<see cref="Providers.SecretReferences.IsAllowedFromApi"/>).
        /// </summary>
        public static bool IsSecretRef(string? value, Providers.DeliveryOptions options)
            => value is null || value.Length <= 500 && !value.Contains('\0') && Providers.SecretReferences.IsAllowedFromApi(value, options);

        /// <summary>A secret named in the deployment's own configuration: any environment variable or file, as the deployment chooses.</summary>
        public static bool IsProvisionedSecretRef(string? value)
            => value is null || value.Length <= 500 && !value.Contains('\0') && Providers.SecretReferences.IsValid(value);

        internal static bool IsSecretRef(string? value, Providers.DeliveryOptions options, bool provisioned)
            => provisioned ? IsProvisionedSecretRef(value) : IsSecretRef(value, options);

        // Secrets the API may not name (the deployment's own, on rows its configuration made or handed back, or saved under wider limits) are used
        // only for what the deployment configures: through the API an API-managed row is saved or enabled only when every secret it would use is
        // one the API may name, the integration's signing secret included for a target. A Put that only disables such a row (everything else
        // left out or sent back as stored) always passes: stopping sends can't wait. Rows the configuration manages change only through it.

        /// <summary>The field to refuse when a Put through the API would use a secret the API may not name; null when every one is in reach.</summary>
        internal static FieldError? SecretOutOfReach(Providers.DeliveryOptions options, string pointer, string? reference, string what, string remedy)
            => IsSecretRef(reference, options) ? null : new FieldError(pointer, $"{what} is one only the deployment may name: {remedy}");

        internal static string SecretRefRule(string what, Providers.DeliveryOptions options, bool provisioned)
            => provisioned ? $"{what} is named: env:NAME, file:/path, vault:path#field or keyvault:name."
                : $"{what} is named, never sent: env:{options.ApiSecretEnvPrefix}NAME, or file: in the deployment's secret folder"
                    + (options.ApiVaultPrefix is { Length: > 0 } v ? $", or vault:{v}path#field" : "")
                    + (options.ApiKeyVaultPrefix is { Length: > 0 } k ? $", or keyvault:{k}name" : "") + ".";

        /// <summary>
        /// A host name, an IP address, or a network in CIDR form, in the form the send-time destination policy reads: a network is parsed the
        /// way it parses it (<see cref="IPNetwork.TryParse(string, out IPNetwork)"/>) and written canonically, so 10.20.1.5/16 is stored as
        /// 10.20.0.0/16 and is one entry with it. Null when it isn't one.
        /// </summary>
        public static string? Destination(string value)
        {
            if (value.Contains('/'))
            {
                if (!IPNetwork.TryParse(value, out var network))
                {
                    return null;
                }
                // The policy compares IPv4 addresses as IPv4 (it unmaps ::ffff:a.b.c.d), so an IPv4-mapped network is stored as the IPv4 one.
                if (network.BaseAddress.IsIPv4MappedToIPv6 && network.PrefixLength >= 96)
                {
                    return new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96).ToString();
                }
                return network.ToString();
            }
            if (IPAddress.TryParse(value, out var address))
            {
                // One address is the network of just that address: the policy matches it as an address, never as a host name's text.
                if (address.IsIPv4MappedToIPv6)
                {
                    address = address.MapToIPv4();
                }
                return new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128).ToString();
            }
            // Host names as the policy sees them at send time: ASCII (punycode for IDN), no trailing dot.
            var host = value.TrimEnd('.');
            if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                return null;
            }
            try
            {
                host = new System.Globalization.IdnMapping().GetAscii(host).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return null;
            }
            return host.Length <= 253 ? host : null;
        }

        /// <summary>A hash of a definition, over JSON in its canonical form: the same content always hashes alike, however it was stored.</summary>
        internal static string Hash(params string?[] parts) => CanonicalJson.Sha256(string.Join("|", parts));

        /// <summary>
        /// Stored JSON (jsonb has its own spacing, key order and number forms) written canonically, numbers included, for hashing: a row hashes
        /// alike before and after a round trip through the database (deferred #9).
        /// </summary>
        internal static string Canonical(string json) => CanonicalJson.WriteNormalized(JsonElement.Parse(json));

        // A definition's hash covers every field a Put can set. A Put compares the hash of what it would store with the stored one before it
        // assigns anything: equal (with the same owner) means nothing changes, so nothing is written or audited. The JSON may read differently from
        // what was sent (jsonb's form), so the comparison is of what it means: the hash.
        // A Put that changes nothing isn't fenced: if another admin changes or deletes the row after it was read, the no-op still answers 200 with
        // what it read. That is a correct order of events (the no-op, then the other change), and fencing it would mean writing the row, which
        // would record an empty change. A Put that does change the row is fenced by the hash, as before.

        internal static string IntegrationHash(string channel, string kind, string settings, string? secretRef, int priority, bool enabled)
            => Hash(channel, kind, Canonical(settings), secretRef, priority.ToString(System.Globalization.CultureInfo.InvariantCulture), enabled.ToString());

        internal static string IntegrationHash(Integration i) => IntegrationHash(i.Channel, i.Kind, i.Settings, i.SecretRef, i.Priority, i.Enabled);

        internal static string TargetHash(string? address, string? addressRef, bool enabled) => Hash(address, addressRef, enabled.ToString());

        internal static string TargetHash(IntegrationTarget t) => TargetHash(t.Address, t.AddressRef, t.Enabled);

        internal static string RouteHash(string match, IEnumerable<string> targets, bool enabled) => Hash(Canonical(match), string.Join(",", targets), enabled.ToString());

        internal static string RouteHash(Route r) => RouteHash(r.Match, r.Targets, r.Enabled);

        /// <summary>A target's address, normalized for its channel, or null when it isn't one (or doesn't fit the column once normalized).</summary>
        public static string? Address(string channel, string address) => channel switch
        {
            Channels.Email => RequestRules.NormalizeAddress(BuiltInChannels.Email, address),
            Channels.Webhook => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo)
                && uri.AbsoluteUri.Length <= 2000 ? uri.AbsoluteUri : null,
            _ => null,
        };

        /// <summary>
        /// The routes of a tenant that name any of the targets <paramref name="targetKeys"/>.
        /// </summary>
        /// <remarks>
        /// What a route names is its <c>routes.targets</c>: every replica keeps that, including one from before <c>route_targets</c> during a
        /// rolling deploy, which saves routes without their rows (or leaves a row for a target it took out). <c>route_targets</c> is the database's
        /// guard against the race between such a check and a route starting to name the target; <see cref="ReleaseStaleRowsAsync"/> clears rows
        /// no route names any more before a delete.
        /// </remarks>
        internal static Task<List<string>> RoutesNamingAsync(NotificationsDbContext db, Guid tenantId, IReadOnlyCollection<string> targetKeys, CancellationToken ct)
            => db.Routes.Where(r => r.TenantId == tenantId && r.Targets.Any(t => targetKeys.Contains(t))).OrderBy(r => r.Key).Select(r => r.Key).ToListAsync(ct);

        /// <summary>
        /// Before targets no route names are deleted: removes their <c>route_targets</c> rows whose route no longer names the target, left by a
        /// replica that changed a route's targets without them. The routes it reads are locked (<c>FOR SHARE</c>): a Put naming the target again
        /// at the same moment holds its route's row, so this waits for it and reads the targets it committed; that row is then kept, and stops
        /// the delete through its foreign key, as it should. Rows already gone are no conflict.
        /// </summary>
        internal static Task<int> ReleaseStaleRowsAsync(NotificationsDbContext db, Guid tenantId, Guid[] targetIds, CancellationToken ct)
            => targetIds.Length == 0 ? Task.FromResult(0) : db.Database.ExecuteSqlAsync($"""
                WITH consulted AS (
                    SELECT r.id, r.targets FROM notifications.routes r
                    WHERE r.tenant_id = {tenantId}
                      AND r.id IN (SELECT route_id FROM notifications.route_targets WHERE tenant_id = {tenantId} AND target_id = ANY({targetIds}))
                    FOR SHARE)
                DELETE FROM notifications.route_targets rt
                USING consulted r, notifications.integration_targets t
                WHERE rt.tenant_id = {tenantId} AND rt.target_id = ANY({targetIds}) AND r.id = rt.route_id
                  AND t.tenant_id = rt.tenant_id AND t.id = rt.target_id
                  AND NOT (t.key = ANY(r.targets))
                """, ct);

        /// <summary>
        /// Links a new target to the routes that already name its key. A route can only be saved naming targets that exist, so these are routes
        /// stored before <c>route_targets</c> named a key that had no target then (the migration had nothing to link); from now on the target is
        /// held as any other.
        /// </summary>
        internal static async Task LinkRoutesNamingAsync(NotificationsDbContext db, IntegrationTarget target, CancellationToken ct)
        {
            var routes = await db.Routes.Where(r => r.TenantId == target.TenantId && r.Targets.Contains(target.Key)).Select(r => new { r.Id, r.Targets }).ToListAsync(ct);
            db.RouteTargets.AddRange(routes.Select(r => new RouteTarget
            {
                TenantId = target.TenantId, RouteId = r.Id, TargetId = target.Id, Position = Array.IndexOf(r.Targets, target.Key),
            }));
        }
    }

    internal sealed partial class ConfigurationHandlers(NotificationsDbContext db, IChannelRegistry channels, IEnumerable<INotificationProvider> providers,
        IEnumerable<IRecipientResolver> resolvers,
        Microsoft.Extensions.Options.IOptions<Providers.DeliveryOptions> delivery, TimeProvider clock) :
        IRequestHandler<PutIntegration, Outcome<IntegrationView>>, IRequestHandler<DeleteIntegration, Outcome<bool>>,
        IRequestHandler<PutTarget, Outcome<TargetView>>, IRequestHandler<DeleteTarget, Outcome<bool>>,
        IRequestHandler<PutRoute, Outcome<RouteView>>, IRequestHandler<DeleteRoute, Outcome<bool>>,
        IRequestHandler<PutAllowlistEntry, Outcome<AllowlistView>>, IRequestHandler<DeleteAllowlistEntry, Outcome<bool>>
    {
        private static Outcome<T> Invalid<T>(params FieldError[] errors) => Outcome<T>.Invalid(ErrorCodes.InvalidConfiguration, "The configuration has errors.", errors);

        /// <summary>The deployment's delivery settings these handlers check against (which secrets the API may name).</summary>
        internal Providers.DeliveryOptions Delivery => delivery.Value;

        /// <summary>Whether the provider of this channel and kind sends the integration's secret with what goes to a target (it signs).</summary>
        internal bool Signs(string? channel, string? kind) => providers.FirstOrDefault(p => p.Channel == channel && p.Kind == kind) is { RequiresSecret: true };

        private static Outcome<T> ConfigManaged<T>(string what)
            => Outcome<T>.Conflict(ErrorCodes.ManagedByConfiguration, $"The {what} comes from the deployment's configuration; change it there.");


        /// <summary>
        /// The refusal when <paramref name="caller"/> (a command's <see cref="IOwnedSetupWrite.ManagedBy"/>, null for the API) may not change or
        /// delete a row <paramref name="owner"/> keeps (phase-5 decision G1); null when it may. Each owner keeps to its own rows; configuration
        /// also takes over the API's, as the provisioning file always has. The service's own rows are refused by their own check.
        /// </summary>
        internal static Outcome<T>? Owned<T>(string? owner, string? caller, string what)
        {
            var by = Caller(caller);
            if (owner is null || owner == by || owner == ManagedBy.Service || owner == ManagedBy.Api && by == ManagedBy.Configuration)
            {
                return null;
            }
            return owner switch
            {
                ManagedBy.Configuration => ConfigManaged<T>(what),
                ManagedBy.WebhooksAlias => Outcome<T>.Conflict(ErrorCodes.ManagedByAlias,
                    $"The {what} belongs to the webhooks alias until cutover; change it through /webhooks."),
                _ => Outcome<T>.Conflict(ErrorCodes.ManagedByApi, $"The {what} was made through the API; it isn't {by}'s to change."),
            };
        }

        /// <summary>A command's owner: the API when it names none. Only the in-process owners may be named.</summary>
        internal static string Caller(string? managedBy) => managedBy switch
        {
            null or ManagedBy.Api => ManagedBy.Api,
            ManagedBy.Configuration or ManagedBy.WebhooksAlias => managedBy,
            _ => throw new ArgumentException($"A setup write is made by the API (null), {ManagedBy.Configuration} or {ManagedBy.WebhooksAlias}, not '{managedBy}'.", nameof(managedBy)),
        };
        private static Outcome<T> ServiceManaged<T>()
            => Outcome<T>.Conflict(ErrorCodes.ManagedByService, $"The integration {Inbox.InboxIntegrations.Key} is the service's own (the inbox); it can't be changed or deleted, and it has no targets.");

        public async ValueTask<Outcome<IntegrationView>> Handle(PutIntegration r, CancellationToken ct)
        {
            if (r.Key == Inbox.InboxIntegrations.Key)
            {
                return ServiceManaged<IntegrationView>(); // a key no one else can have: the answer is that it's the service's, not that it's malformed
            }
            if (!ConfigurationRules.Key().IsMatch(r.Key))
            {
                return Invalid<IntegrationView>(new FieldError("/key", "A key is 1–63 lowercase letters, digits or hyphens, starting with a letter."));
            }
            var existing = await db.Integrations.SingleOrDefaultAsync(i => i.TenantId == r.NotificationTenantId && i.Key == r.Key, ct);
            if (Owned<IntegrationView>(existing?.ManagedBy, r.ManagedBy, "integration " + r.Key) is { } owned)
            {
                return owned;
            }
            if (existing?.ManagedBy == ManagedBy.Service)
            {
                return ServiceManaged<IntegrationView>();
            }
            if (r.Id == Guid.Empty)
            {
                return Outcome<IntegrationView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/id", "An integration's id is a real id, not the empty one; leave it out for a new one.")]);
            }
            // An id is the caller's for a new integration only (M46-2): an existing one keeps its own, and one another integration has is taken.
            if (r.Id is { } id && (existing is not null ? existing.Id != id : await db.Integrations.AnyAsync(i => i.TenantId == r.NotificationTenantId && i.Id == id, ct)))
            {
                return Outcome<IntegrationView>.Conflict(ErrorCodes.IdInUse, existing is not null
                    ? $"The integration {r.Key} has another id; an integration's id doesn't change."
                    : $"Another integration of this tenant has the id {id}.");
            }
            // From configuration the file states the secret: one it leaves out is none, not the stored one. Through the API, one sent back as it
            // is stored is no new secret (a client that PUTs back what it read).
            var sentSecret = !r.Provisioned && existing is not null && r.SecretRef == existing.SecretRef ? null : r.SecretRef;
            var secretRef = r.Provisioned ? r.SecretRef : sentSecret ?? existing?.SecretRef;
            var channel = r.Channel ?? existing?.Channel;
            var errors = new List<FieldError>();
            if (channel is null || channels.Find(channel) is null)
            {
                errors.Add(new FieldError("/channel", "The channel is one of the registered channels."));
            }
            else if (channel == Channels.Inbox && existing is null)
            {
                errors.Add(new FieldError("/channel", "The inbox is the service's own channel: every tenant has its integration."));
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
            // Unstorable first, whatever the kind: everything after reads the strings, and reading half a surrogate pair throws.
            var unstorable = r.Settings is { } sent ? CanonicalJson.Unstorable(sent, "/settings") : null;
            if (unstorable is not null)
            {
                errors.Add(unstorable);
            }
            else if (r.Settings is { } settings && settings.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new FieldError("/settings", "Settings are a JSON object."));
            }
            else if (r.Settings is { } sent2 && CanonicalJson.FirstDuplicate(sent2, "/settings") is { } duplicate)
            {
                // jsonb keeps the last of repeated names: what's stored would differ from what was checked.
                errors.Add(new FieldError(duplicate, "This object repeats a property name; each name appears once."));
            }
            var settingsJson = r.Settings is { } s && unstorable is null ? CanonicalJson.Write(s) : existing?.Settings ?? "{}";
            var sameSettings = existing is not null && (r.Settings is null
                || r.Settings.Value.ValueKind == JsonValueKind.Object && ConfigurationRules.Canonical(settingsJson) == ConfigurationRules.Canonical(existing.Settings));
            var (priority, enabled) = (r.Priority ?? existing?.Priority ?? 0, r.Enabled ?? existing?.Enabled ?? true);
            // Everything as stored but enabled: false. Always allowed through the API, so sends can be stopped whatever the row holds.
            var disablesOnly = !r.Provisioned && existing is not null && !enabled && channel == existing.Channel && kind == existing.Kind && sameSettings
                && secretRef == existing.SecretRef && priority == existing.Priority;
            if (!ConfigurationRules.IsSecretRef(sentSecret, delivery.Value, r.Provisioned))
            {
                errors.Add(new FieldError("/secretRef", ConfigurationRules.SecretRefRule("A secret", delivery.Value, r.Provisioned)));
            }
            else if (provider is { RequiresSecret: true } && secretRef is null)
            {
                // Webhooks are always signed (D14): an integration without its signing secret is refused, not sent unsigned.
                errors.Add(new FieldError("/secretRef", $"A {kind} integration signs what it sends: name its secret. "
                    + ConfigurationRules.SecretRefRule("It", delivery.Value, r.Provisioned)));
            }
            else if (!r.Provisioned && !disablesOnly
                && ConfigurationRules.SecretOutOfReach(delivery.Value, "/secretRef", secretRef, "The stored secret", ConfigurationRules.SecretRefRule("name one: it", delivery.Value, false)) is { } outOfReach)
            {
                errors.Add(outOfReach);
            }
            // Checked when they are new or change, or the kind does: a stored integration saved under an older check can still be disabled or
            // reprioritized without rewriting its settings, sent back as stored or left out.
            var settingsChange = existing is null || !sameSettings || kind != existing.Kind;
            if (provider is not null && errors.Count == 0 && settingsChange
                && provider.CheckSettings(r.Settings is { } given ? given.GetRawText() : existing?.Settings ?? "{}") is { } settingsProblem)
            {
                errors.Add(new FieldError("/settings", settingsProblem));
            }
            if (errors.Count > 0)
            {
                return Invalid<IntegrationView>([.. errors]);
            }
            var now = clock.UtcNow();
            var managedBy = Caller(r.ManagedBy);
            var hash = ConfigurationRules.IntegrationHash(channel!, kind!, settingsJson, secretRef, priority, enabled);
            if (existing is null)
            {
                existing = new Integration
                {
                    TenantId = r.NotificationTenantId, Id = r.Id ?? Guid.CreateVersion7(), Key = r.Key, Channel = channel!, Kind = kind!, Settings = settingsJson, SecretRef = secretRef,
                    Priority = priority, Enabled = enabled, ManagedBy = managedBy, DefinitionHash = hash, UpdatedAt = now,
                };
                db.Integrations.Add(existing);
            }
            else if ((existing.DefinitionHash, existing.ManagedBy) != (hash, managedBy))
            {
                existing.Kind = kind!;
                existing.Settings = settingsJson;
                existing.SecretRef = secretRef;
                existing.Priority = priority;
                existing.Enabled = enabled;
                existing.ManagedBy = managedBy;
                existing.UpdatedAt = now;
                existing.DefinitionHash = hash;
            }
            return Outcome<IntegrationView>.Ok(await ConfigurationQueries.IntegrationAsync(db, existing, ct));
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteIntegration r, CancellationToken ct)
        {
            var existing = await db.Integrations.SingleOrDefaultAsync(i => i.TenantId == r.NotificationTenantId && i.Key == r.Key, ct);
            if (existing is null)
            {
                return Outcome<bool>.Ok(false);
            }
            if (Owned<bool>(existing.ManagedBy, r.ManagedBy, "integration " + r.Key) is { } owned)
            {
                return owned;
            }
            if (existing.ManagedBy == ManagedBy.Service)
            {
                return ServiceManaged<bool>();
            }
            if (await db.Deliveries.AnyAsync(d => d.TenantId == r.NotificationTenantId && d.IntegrationId == existing.Id, ct))
            {
                return Outcome<bool>.Conflict(ErrorCodes.IntegrationInUse, "Deliveries went through this integration; disable it instead, so their history keeps it.");
            }
            // Its targets go with it: no route may still name one, or it would match and plan recipients nothing can reach.
            var targets = await db.IntegrationTargets.Where(t => t.TenantId == r.NotificationTenantId && t.IntegrationId == existing.Id)
                .Select(t => new { t.Id, t.Key }).ToListAsync(ct);
            if (targets.Count > 0 && await ConfigurationRules.RoutesNamingAsync(db, r.NotificationTenantId, [.. targets.Select(t => t.Key)], ct) is { Count: > 0 } routes)
            {
                return Outcome<bool>.Conflict(ErrorCodes.TargetInUse, $"Routes name this integration's targets: {string.Join(", ", routes)}. Change or delete them first.");
            }
            await ConfigurationRules.ReleaseStaleRowsAsync(db, r.NotificationTenantId, [.. targets.Select(t => t.Id)], ct);
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
            if (integration.ManagedBy == ManagedBy.Service)
            {
                return ServiceManaged<TargetView>();
            }
            var existing = await db.IntegrationTargets.SingleOrDefaultAsync(t => t.TenantId == r.NotificationTenantId && t.Key == r.Key, ct);
            if (existing is not null && existing.IntegrationId != integration.Id)
            {
                // Through the API a target stays with its integration. The file states where each target is, so it may move one to another
                // integration of the same channel: the target is kept (its id, so its deliveries' destination key and history still name it).
                var from = r.Provisioned
                    ? await db.Integrations.AsNoTracking().Where(i => i.TenantId == r.NotificationTenantId && i.Id == existing.IntegrationId).Select(i => i.Channel).SingleAsync(ct)
                    : null;
                if (from != integration.Channel)
                {
                    return Outcome<TargetView>.Conflict(ErrorCodes.TargetExists, r.Provisioned
                        ? $"The target key '{r.Key}' is a {from} target; a target moves only between integrations of one channel. Give the {integration.Channel} target another key."
                        : $"The target key '{r.Key}' is taken by another integration; target keys are unique in a tenant.");
                }
            }
            if (Owned<TargetView>(existing?.ManagedBy, r.ManagedBy, "target " + r.Key) is { } owned)
            {
                return owned;
            }
            string? address = null;
            var own = providers.FirstOrDefault(p => p.Channel == integration.Channel && p.Kind == integration.Kind) as ITargetAddresses;
            if (r.Address is not null && (address = own is not null ? own.NormalizeTarget(r.Address) : ConfigurationRules.Address(integration.Channel, r.Address)) is null)
            {
                return Invalid<TargetView>(new FieldError("/address", own?.TargetRule ?? (integration.Channel == Channels.Email
                    ? "The address is a bare email address." : "The address is an absolute http or https URL without credentials.")));
            }
            // Through the API, a secret address sent back as it is stored is no new one (a client that PUTs back what it read).
            var sentAddressRef = !r.Provisioned && existing is not null && r.AddressRef == existing.AddressRef ? null : r.AddressRef;
            if (!ConfigurationRules.IsSecretRef(sentAddressRef, delivery.Value, r.Provisioned))
            {
                return Invalid<TargetView>(new FieldError("/addressRef", ConfigurationRules.SecretRefRule("A secret address", delivery.Value, r.Provisioned)));
            }
            // From the API, a Put that names neither keeps the stored address; from configuration the file states both.
            var replaceAddress = r.Provisioned || address is not null || r.AddressRef is not null;
            if ((replaceAddress ? address : existing?.Address) is null && (replaceAddress ? r.AddressRef : existing?.AddressRef) is null)
            {
                return Invalid<TargetView>(new FieldError("/address", "A target has an address, or an addressRef naming where its secret address is."));
            }
            var (newAddress, newAddressRef) = replaceAddress ? (address, r.AddressRef) : (existing!.Address, existing.AddressRef);
            var enabled = r.Enabled ?? existing?.Enabled ?? true;
            // Everything as stored but enabled: false. Always allowed through the API, so sends can be stopped whatever the row holds.
            var disablesOnly = !r.Provisioned && existing is not null && !enabled && existing.IntegrationId == integration.Id
                && newAddress == existing.Address && newAddressRef == existing.AddressRef;
            if (!r.Provisioned && !disablesOnly)
            {
                // The target's own secret address, and the integration's signing secret, which would go to whatever address an admin sets here.
                if (ConfigurationRules.SecretOutOfReach(delivery.Value, "/addressRef", newAddressRef, "The stored secret address",
                        ConfigurationRules.SecretRefRule("name one: it", delivery.Value, false)) is { } addressOutOfReach)
                {
                    return Invalid<TargetView>(addressOutOfReach);
                }
                // Only a secret the provider sends with what goes to the target's address (a webhook's signing key). An SMTP password goes to the
                // deployment's mail server, never to a target.
                if (Signs(integration.Channel, integration.Kind) &&ConfigurationRules.SecretOutOfReach(delivery.Value, "/integration", integration.SecretRef, $"The signing secret of integration {integration.Key}",
                        integration.ManagedBy == ManagedBy.Configuration
                            ? $"its targets are set in the deployment's configuration, under {integration.Key}"
                            : $"give {integration.Key} a secret of yours first (its secretRef), then its targets") is { } integrationOutOfReach)
                {
                    return Invalid<TargetView>(integrationOutOfReach);
                }
            }
            var now = clock.UtcNow();
            var managedBy = Caller(r.ManagedBy);
            var hash = ConfigurationRules.TargetHash(newAddress, newAddressRef, enabled);
            if (existing is null)
            {
                existing = new IntegrationTarget
                {
                    TenantId = r.NotificationTenantId, IntegrationId = integration.Id, Key = r.Key, Address = newAddress, AddressRef = newAddressRef,
                    Enabled = enabled, ManagedBy = managedBy, DefinitionHash = hash, UpdatedAt = now,
                };
                db.IntegrationTargets.Add(existing);
                await ConfigurationRules.LinkRoutesNamingAsync(db, existing, ct);
            }
            else if ((existing.DefinitionHash, existing.ManagedBy, existing.IntegrationId) != (hash, managedBy, integration.Id))
            {
                existing.IntegrationId = integration.Id;
                existing.Address = newAddress;
                existing.AddressRef = newAddressRef;
                existing.Enabled = enabled;
                existing.ManagedBy = managedBy;
                existing.UpdatedAt = now;
                existing.DefinitionHash = hash;
            }
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
            if (Owned<bool>(existing.ManagedBy, r.ManagedBy, "target " + r.Key) is { } owned)
            {
                return owned;
            }
            if (await ConfigurationRules.RoutesNamingAsync(db, r.NotificationTenantId, [existing.Key], ct) is { Count: > 0 } routes)
            {
                return Outcome<bool>.Conflict(ErrorCodes.TargetInUse, $"Routes name this target: {string.Join(", ", routes)}. Change or delete them first.");
            }
            await ConfigurationRules.ReleaseStaleRowsAsync(db, r.NotificationTenantId, [existing.Id], ct);
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
            if (Owned<RouteView>(existing?.ManagedBy, r.ManagedBy, "route " + r.Key) is { } owned)
            {
                return owned;
            }
            var errors = new List<FieldError>();
            string? match = null;
            if (r.Match is { } m && CanonicalJson.Unstorable(m, "/match") is { } unstorable)
            {
                errors.Add(unstorable);
            }
            else if (r.Match is { } m1 && CanonicalJson.FirstDuplicate(m1, "/match") is { } duplicate)
            {
                // jsonb keeps the last of repeated names: the planner would match on something other than what was checked.
                errors.Add(new FieldError(duplicate, "This object repeats a property name; each name appears once."));
            }
            else if (r.Match is { } m2)
            {
                var (parsed, matchErrors) = RouteMatch.Parse(m2);
                errors.AddRange(matchErrors);
                match = parsed is null ? null : CanonicalJson.Write(m2);
            }
            else if (existing is null)
            {
                errors.Add(new FieldError("/match", "A route needs a match; {} matches every notification."));
            }
            var targets = r.Targets?.Distinct(StringComparer.Ordinal).ToArray();
            Dictionary<string, Guid> known = [];
            if (targets is not null)
            {
                if (targets.Length is 0 or > 50)
                {
                    errors.Add(new FieldError("/targets", "A route has 1–50 targets."));
                }
                known = await db.IntegrationTargets.Where(t => t.TenantId == r.NotificationTenantId && targets.Contains(t.Key))
                    .ToDictionaryAsync(t => t.Key, t => t.Id, ct);
                for (var i = 0; i < r.Targets!.Count; i++)
                {
                    if (!known.ContainsKey(r.Targets[i]))
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
            var managedBy = Caller(r.ManagedBy);
            var (newMatch, newTargets, enabled) = (match ?? existing!.Match, targets ?? existing!.Targets, r.Enabled ?? existing?.Enabled ?? true);
            var hash = ConfigurationRules.RouteHash(newMatch, newTargets, enabled);
            List<RouteTarget> rows = [];
            HashSet<Guid> named = [];
            var changed = existing is null || (existing.DefinitionHash, existing.ManagedBy) != (hash, managedBy);
            if (existing is null)
            {
                existing = new Route
                {
                    TenantId = r.NotificationTenantId, Key = r.Key, Match = newMatch, Targets = newTargets, Enabled = enabled, ManagedBy = managedBy,
                    DefinitionHash = hash, UpdatedAt = now,
                };
                db.Routes.Add(existing);
            }
            else
            {
                if (targets is not null)
                {
                    // The targets the route names as read, before this Put changes them: its other rows are stale (below).
                    var before = existing.Targets;
                    named = [.. await db.IntegrationTargets.Where(t => t.TenantId == r.NotificationTenantId && before.Contains(t.Key)).Select(t => t.Id).ToListAsync(ct)];
                }
                if (changed)
                {
                    existing.Match = newMatch;
                    existing.Targets = newTargets;
                    existing.Enabled = enabled;
                    existing.ManagedBy = managedBy;
                    existing.UpdatedAt = now;
                    existing.DefinitionHash = hash;
                }
                if (targets is not null)
                {
                    rows = await db.RouteTargets.Where(x => x.TenantId == r.NotificationTenantId && x.RouteId == existing.Id).ToListAsync(ct);
                }
            }
            if (targets is not null)
            {
                // The rows the database checks (deferred #8): a target deleted meanwhile fails this route's commit, not just its planning. Kept in
                // step even when the route itself is unchanged, so a Put of the same route repairs rows that went missing. When the route changes,
                // every row it keeps is written (its position), so one removed under this Put since it was read fails it, rather than the route
                // newly naming a target no row holds (a stale-row release racing it, ReleaseStaleRowsAsync). A Put that changes nothing writes
                // nothing, as for every Put.
                // A stale row (one for a target the route doesn't name, left by an older replica) that this Put no longer needs is left alone:
                // a release deletes exactly those (ReleaseStaleRowsAsync, when its target goes), and runs in its own statement, not in this
                // Put's transaction, which only begins after this handler. Deleting it here too failed the Put (409) when a release took it
                // between this read and the save (deferred #24). Left, it holds nothing a delete doesn't release first.
                var positions = targets.Select((key, i) => (Id: known[key], Position: i)).ToDictionary(t => t.Id, t => t.Position);
                db.RouteTargets.RemoveRange(rows.Where(x => !positions.ContainsKey(x.TargetId) && named.Contains(x.TargetId)));
                foreach (var kept in rows.Where(x => positions.ContainsKey(x.TargetId)))
                {
                    if (changed || kept.Position != positions[kept.TargetId])
                    {
                        kept.Position = positions[kept.TargetId];
                        db.Entry(kept).Property(x => x.Position).IsModified = true;
                    }
                }
                db.RouteTargets.AddRange(positions.Where(p => rows.All(x => x.TargetId != p.Key))
                    .Select(p => new RouteTarget { TenantId = r.NotificationTenantId, RouteId = existing.Id, TargetId = p.Key, Position = p.Value }));
            }
            return Outcome<RouteView>.Ok(ConfigurationQueries.Route(existing));
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteRoute r, CancellationToken ct)
        {
            var existing = await db.Routes.SingleOrDefaultAsync(x => x.TenantId == r.NotificationTenantId && x.Key == r.Key, ct);
            if (existing is null)
            {
                return Outcome<bool>.Ok(false);
            }
            if (Owned<bool>(existing.ManagedBy, r.ManagedBy, "route " + r.Key) is { } owned)
            {
                return owned;
            }
            db.Routes.Remove(existing);
            return Outcome<bool>.Ok(true);
        }

        public async ValueTask<Outcome<AllowlistView>> Handle(PutAllowlistEntry r, CancellationToken ct)
        {
            if (ConfigurationRules.Destination(r.Destination.Trim().ToLowerInvariant()) is not { } destination)
            {
                return Invalid<AllowlistView>(new FieldError("/destination",
                    "A destination is a host name, an IP address, or a network in CIDR form (10.20.0.0/16)."));
            }
            var ports = r.Ports?.Distinct().Order().ToArray() ?? [];
            if (ports.Any(p => p is < 1 or > 65535) || ports.Length > 20)
            {
                return Invalid<AllowlistView>(new FieldError("/ports", "Up to 20 ports, each 1–65535; none means 443 only."));
            }
            // An entry for what is never reachable (loopback, link-local, metadata) would look like it works and never would.
            if (Providers.DestinationPolicy.ValidateEntry(new Providers.AllowedDestination(destination, ports)) is { } refused)
            {
                return Invalid<AllowlistView>(new FieldError("/destination", refused));
            }
            var existing = await db.Allowlist.SingleOrDefaultAsync(a => a.TenantId == r.NotificationTenantId && a.Destination == destination, ct);
            if (Owned<AllowlistView>(existing?.ManagedBy, r.ManagedBy, "allowlist entry " + destination) is { } owned)
            {
                return owned;
            }
            var managedBy = Caller(r.ManagedBy);
            if (existing is null)
            {
                existing = new AllowlistEntry { TenantId = r.NotificationTenantId, Destination = destination };
                db.Allowlist.Add(existing);
            }
            else if (existing.Ports.SequenceEqual(ports) && existing.ManagedBy == managedBy)
            {
                return Outcome<AllowlistView>.Ok(ConfigurationQueries.Allowlist(existing));
            }
            existing.Ports = ports;
            existing.ManagedBy = managedBy;
            existing.UpdatedAt = clock.UtcNow();
            return Outcome<AllowlistView>.Ok(ConfigurationQueries.Allowlist(existing));
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteAllowlistEntry r, CancellationToken ct)
        {
            var given = r.Destination.Trim().ToLowerInvariant();
            var destination = ConfigurationRules.Destination(given) ?? given;
            var existing = await db.Allowlist.SingleOrDefaultAsync(a => a.TenantId == r.NotificationTenantId && a.Destination == destination, ct);
            if (existing is null)
            {
                return Outcome<bool>.Ok(false);
            }
            if (Owned<bool>(existing.ManagedBy, r.ManagedBy, "allowlist entry " + destination) is { } owned)
            {
                return owned;
            }
            db.Allowlist.Remove(existing);
            return Outcome<bool>.Ok(true);
        }
    }

    public static partial class ConfigurationQueries
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
    public sealed partial class ConfigurationReader(NotificationsDbContext db)
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
