using System.Text.Json;
using System.Text.Json.Serialization;
using Juice.EF.Extensions;
using Juice.MediatR;
using Juice.Notifications.Data;
using Juice.Notifications.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Configuration
{
    /// <summary>
    /// Configuration section <c>Provisioning</c> (phase-1-plan I12): a JSON file holding tenants' integrations, targets, routes and allowlist, loaded
    /// at start. A file, not configuration keys: settings and route matches are JSON whose numbers and booleans must stay what they are, and
    /// configuration providers turn every value into a string.
    /// </summary>
    public sealed class ProvisioningOptions
    {
        public const string Section = "Provisioning";

        /// <summary>
        /// The file. None means provisioning is off: rows a file provisioned before are left as they are (a missing setting must not disable them).
        /// To hand rows back to the API, load a file that no longer lists them.
        /// </summary>
        public string? Path { get; set; }
    }

    /// <summary>The provisioning file: <c>{ "tenants": { "&lt;tenant id&gt;": { "integrations": …, "routes": …, "allowlist": …, "resolvers": … } } }</c>.</summary>
    public sealed record ProvisioningFile(Dictionary<Guid, TenantProvisioning>? Tenants);

    /// <summary>One tenant's setup. Integrations and routes are keyed by their key; targets sit under their integration.</summary>
    public sealed record TenantProvisioning(Dictionary<string, ProvisionedIntegration>? Integrations, Dictionary<string, ProvisionedRoute>? Routes,
        List<ProvisionedAllowlistEntry>? Allowlist, Dictionary<string, ProvisionedResolver>? Resolvers = null);

    public sealed record ProvisionedIntegration(string? Channel, string? Kind, JsonElement? Settings, string? SecretRef, int? Priority, bool? Enabled,
        Dictionary<string, ProvisionedTarget>? Targets);

    public sealed record ProvisionedTarget(string? Address, string? AddressRef, bool? Enabled);

    public sealed record ProvisionedRoute(JsonElement? Match, List<string>? Targets, bool? Enabled, int? DigestSeconds = null);

    public sealed record ProvisionedAllowlistEntry(string? Destination, List<int>? Ports);

    /// <summary>A resolver for the people kinds (phase-2-plan M10); handles defaults to every kind its implementation can.</summary>
    public sealed record ProvisionedResolver(string? Kind, List<string>? Handles, JsonElement? Settings, string? SecretRef, bool? Enabled);

    /// <summary>
    /// Wraps one tenant's provisioning, so a host can record it: the audit host opens an audit scope (Juice.Notifications.Api). By default it just
    /// runs it.
    /// </summary>
    public interface IProvisioningScope
    {
        ValueTask RunAsync(Guid tenantId, Func<ValueTask> work);
    }

    internal sealed class UnrecordedProvisioningScope : IProvisioningScope
    {
        public ValueTask RunAsync(Guid tenantId, Func<ValueTask> work) => work();
    }

    /// <summary>
    /// Makes the database match the provisioning file, at start (phase-1-plan I12, as Juice.Observability's <c>WebhookProvisioner</c>):
    /// <list type="bullet">
    /// <item>Every row goes through the API's own checks (the configuration handlers), so the file can't hold what the API would refuse; a
    /// secret it names may be any environment variable or file, as the deployment's own configuration.</item>
    /// <item>The file owns what it lists (<c>ManagedBy = config</c>), taking over a key the API made; the API refuses changes to those rows.</item>
    /// <item>What it no longer lists is handed back to the API: integrations, targets and routes disabled (deliveries' history keeps them),
    /// allowlist entries removed. What the API made and the file never listed is left alone.</item>
    /// <item>Each tenant in one transaction, one replica at a time (an advisory lock), and only rows whose definition changed are written; every
    /// change is audited as a data change under the actor <see cref="Actor"/>.</item>
    /// <item>A file that can't be read, or holds anything a check refuses, stops the host: a tenant before it may already be applied, none after.</item>
    /// </list>
    /// </summary>
    public sealed class ConfigurationProvisioner(IServiceScopeFactory scopes, IOptions<ProvisioningOptions> options, TimeProvider clock,
        ILogger<ConfigurationProvisioner> logger, NotificationsHosting? hosting = null) : IHostedService
    {
        /// <summary>Who provisioned changes are recorded as.</summary>
        public const string Actor = "deployment-config";

        private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            // Names as written (camelCase): "Enabled" next to "enabled" would otherwise both bind, the last winning unseen by the duplicate check.
            PropertyNameCaseInsensitive = false,
            // Numbers as numbers: the web defaults would also read "5" for a priority or a port.
            NumberHandling = JsonNumberHandling.Strict,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            List<Guid> owning;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                // A schema not migrated (Database:Migrate off, nothing migrated it) is said in words, not as a missing table.
                if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList() is { Count: > 0 } pending)
                {
                    throw new InvalidOperationException($"The notifications schema isn't migrated ({pending.Count} migration(s) pending, the next is {pending[0]}): "
                        + "set Database:Migrate, or migrate it before the host starts. Nothing was provisioned.");
                }
                owning = await db.Integrations.Where(i => i.ManagedBy == ManagedBy.Configuration).Select(i => i.TenantId)
                    .Union(db.IntegrationTargets.Where(t => t.ManagedBy == ManagedBy.Configuration).Select(t => t.TenantId))
                    .Union(db.Routes.Where(r => r.ManagedBy == ManagedBy.Configuration).Select(r => r.TenantId))
                    .Union(db.Allowlist.Where(a => a.ManagedBy == ManagedBy.Configuration).Select(a => a.TenantId))
                    .Union(db.Resolvers.Where(c => c.ManagedBy == ManagedBy.Configuration).Select(c => c.TenantId))
                    .ToListAsync(cancellationToken);
            }
            if (string.IsNullOrWhiteSpace(options.Value.Path))
            {
                // No file is not an empty file: a replica started without the setting (a lost variable or volume) must not disable every
                // provisioned route. A deployment that means to hand its rows back loads a file with no tenants.
                if (owning.Count > 0)
                {
                    logger.LogWarning("Provisioning:Path isn't set, but {Tenants} tenants have rows provisioned from configuration; they are left as they are. "
                        + "To hand them back to the API, load a file without them.", owning.Count);
                }
                return;
            }
            var listed = Load(options.Value.Path).Tenants ?? [];
            // An embedded host has one tenant: a file naming another is refused before anything is written, never half-applied or skipped.
            if (hosting is { Mode: NotificationsMode.Embedded, EmbeddedTenantId: { } own } && listed.Keys.Where(k => k != own).Order().ToList() is { Count: > 0 } foreign)
            {
                throw new InvalidOperationException($"Provisioning:Path '{options.Value.Path}' names tenant {foreign[0]}, but this embedded host's tenant is {own}; "
                    + "the file may name only the host's tenant. Nothing was provisioned.");
            }
            // An embedded host leaves another tenant's rows alone (they aren't its to prune or disable): said, and not written.
            if (hosting is { Mode: NotificationsMode.Embedded, EmbeddedTenantId: { } host } && owning.Where(t => t != host).ToList() is { Count: > 0 } strangers)
            {
                logger.LogWarning("Embedded provisioning leaves the rows of {Tenants} other tenant(s) alone; this host's tenant is {Host}", strangers.Count, host);
                owning = [.. owning.Where(t => t == host)];
            }
            // Tenants the file lists, and tenants it no longer lists that still have rows it owned.
            HashSet<Guid> tenants = [.. listed.Keys, .. owning];
            var changes = 0;
            foreach (var tenantId in tenants.Order())
            {
                changes += await ProvisionTenantAsync(tenantId, listed.GetValueOrDefault(tenantId) ?? new TenantProvisioning(null, null, null), cancellationToken);
            }
            if (tenants.Count > 0)
            {
                logger.LogInformation("Configuration provisioned: {Tenants} tenants, {Changes} changes", tenants.Count, changes);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>The file, read and checked for shape; an empty one when no path is configured.</summary>
        internal static ProvisioningFile Load(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new ProvisioningFile(null);
            }
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Provisioning:Path '{path}' can't be read: {ex.Message}", ex);
            }
            try
            {
                // Repeated names would be read as the last one, and the file would hold something other than what its reader sees. So would one
                // tenant written twice in different letter case: two names to that check, one key once read.
                using (var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
                {
                    if (CanonicalJson.FirstDuplicate(document.RootElement, "") is { } duplicate)
                    {
                        throw new InvalidOperationException($"Invalid provisioning file '{path}': {duplicate} repeats a property name; each name appears once.");
                    }
                    var tenantIds = new HashSet<Guid>();
                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("tenants", out var tenants)
                        && tenants.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var tenant in tenants.EnumerateObject())
                        {
                            if (Guid.TryParse(tenant.Name, out var id) && !tenantIds.Add(id))
                            {
                                throw new InvalidOperationException($"Invalid provisioning file '{path}': the tenant {id} is listed twice.");
                            }
                        }
                    }
                    return document.RootElement.Deserialize<ProvisioningFile>(FileJson) ?? new ProvisioningFile(null);
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid provisioning file '{path}': {ex.Message}", ex);
            }
        }

        private async Task<int> ProvisionTenantAsync(Guid tenantId, TenantProvisioning definition, CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<NotificationsDbContext>();
            var handlers = ActivatorUtilities.CreateInstance<ConfigurationHandlers>(services);
            var mediator = services.GetRequiredService<IMediator>();
            var changes = 0;
            using var context = NotificationsMessageContext.Begin("provisioning");
            await services.GetRequiredService<IProvisioningScope>().RunAsync(tenantId, async () =>
            {
                // The steps of Juice's TransactionBehavior, by hand, and not a command through the mediator: the behavior opens its transaction
                // after the handler has run, and provisioning must hold its lock and save each step (a route's checks read the targets saved
                // before it) inside one transaction, or a tenant could be left half-applied. What matters is kept: the context is managed, so its
                // audit events carry the transaction and are reported only once it commits; nothing here publishes, so there is no outbox to save.
                db.BeginManage();
                try
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('juice-notifications:provisioning'))", ct);
                    var problems = new List<string>();
                    changes = await ApplyAsync(db, handlers, tenantId, definition, clock.UtcNow(), problems, ct);
                    if (problems.Count > 0)
                    {
                        throw new InvalidOperationException($"Invalid provisioning for tenant {tenantId}:\n" + string.Join("\n", problems));
                    }
                    await mediator.DispatchEventsUntilStableAsync(db, logger, cancellationToken: ct);
                    await db.CommitTransactionAsync(transaction.TransactionId, ct);
                    db.ClearEvents();
                }
                catch (Exception ex) when (ex is not InvalidOperationException && DbConflicts.Classify(ex) is { } conflict)
                {
                    // Another writer (an admin's PUT, another replica) changed a row between this pass's read and its save. The transaction
                    // rolls back, so nothing of this tenant is applied; the next start reconciles. Said as the API says it, not as a raw EF error.
                    throw new InvalidOperationException($"Provisioning for tenant {tenantId} lost a race with another writer ({ErrorCodes.ConcurrentChange}, {conflict.Kind}): "
                        + "nothing of it was applied; the next start reconciles.", ex);
                }
                finally
                {
                    db.EndManage();
                }
            });
            return changes;
        }

        /// <summary>Applies one tenant's definition, saving as it goes (inside the caller's transaction); returns the rows written.</summary>
        internal static async Task<int> ApplyAsync(NotificationsDbContext db, ConfigurationHandlers handlers, Guid tenantId, TenantProvisioning definition,
            DateTimeOffset now, List<string> problems, CancellationToken ct)
        {
            var changes = 0;
            async Task<T?> Put<T>(string what, ValueTask<Outcome<T>> put)
            {
                var outcome = await put;
                if (outcome.Kind is not (OutcomeKind.Ok or OutcomeKind.Created))
                {
                    var details = outcome.Errors is { Count: > 0 } errors ? string.Join("; ", errors.Select(e => $"{e.Pointer}: {e.Message}")) : outcome.Message;
                    problems.Add($"{what}: {details}");
                    db.ChangeTracker.Clear();
                    return default;
                }
                changes += await db.SaveChangesAsync(ct) > 0 ? 1 : 0;
                return outcome.Value;
            }

            var integrations = definition.Integrations ?? [];
            foreach (var (key, i) in integrations)
            {
                await Put($"integration '{key}'", handlers.Handle(new PutIntegration(tenantId, key, i.Channel, i.Kind,
                    i.Settings ?? JsonElement.Parse("{}"), i.SecretRef, i.Priority ?? 0, i.Enabled ?? true) { ManagedBy = ManagedBy.Configuration }, ct));
            }
            var targetKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (integrationKey, i) in integrations)
            {
                foreach (var (key, t) in i.Targets ?? [])
                {
                    if (!targetKeys.Add(key))
                    {
                        problems.Add($"target '{key}': listed under two integrations; target keys are unique in a tenant");
                        continue;
                    }
                    await Put($"target '{key}' of integration '{integrationKey}'", handlers.Handle(new PutTarget(tenantId, integrationKey, key, t.Address,
                        t.AddressRef, t.Enabled ?? true) { ManagedBy = ManagedBy.Configuration }, ct));
                }
            }
            // A secret only the deployment may name signs only for what the deployment configures: an integration given one may not keep enabled
            // targets the API made (when the file takes over an API integration). The file lists them too, or they go first.
            // Only an enabled one (nothing is signed while it is disabled; enabling it in the file later is checked then), and only where the
            // provider sends the secret to the targets (a webhook's signing key; not an SMTP password).
            foreach (var (key, i) in integrations.Where(p => (p.Value.Enabled ?? true) && !ConfigurationRules.IsSecretRef(p.Value.SecretRef, handlers.Delivery)
                && handlers.Signs(p.Value.Channel, p.Value.Kind)))
            {
                var apiTargets = await (from t in db.IntegrationTargets
                                        join owner in db.Integrations on new { t.TenantId, Id = t.IntegrationId } equals new { owner.TenantId, owner.Id }
                                        where t.TenantId == tenantId && owner.Key == key && t.ManagedBy == ManagedBy.Api && t.Enabled
                                        orderby t.Key
                                        select t.Key).ToListAsync(ct);
                if (apiTargets.Count > 0)
                {
                    problems.Add($"integration '{key}': its secret is one only the deployment may name, and it would sign for targets the API made "
                        + $"({string.Join(", ", apiTargets)}). List them under it in the file, or disable or remove them through the API first.");
                }
            }
            var routes = definition.Routes ?? [];
            foreach (var (key, r) in routes)
            {
                // The file states the whole route: a missing match or targets list isn't "keep what's stored".
                if (r.Match is null || r.Targets is null)
                {
                    problems.Add($"route '{key}': a route has a match ({{}} matches every notification) and its targets");
                    continue;
                }
                await Put($"route '{key}'", handlers.Handle(new PutRoute(tenantId, key, r.Match, r.Targets, r.Enabled ?? true, r.DigestSeconds ?? 0) { ManagedBy = ManagedBy.Configuration }, ct));
            }
            var destinations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in definition.Allowlist ?? [])
            {
                var view = await Put($"allowlist entry '{a.Destination}'", handlers.Handle(new PutAllowlistEntry(tenantId, a.Destination ?? "", a.Ports)
                    { ManagedBy = ManagedBy.Configuration }, ct));
                if (view is not null && !destinations.Add(view.Destination))
                {
                    problems.Add($"allowlist entry '{a.Destination}': listed twice (as {view.Destination})");
                }
            }
            var resolverDefinitions = definition.Resolvers ?? [];
            foreach (var (key, c) in resolverDefinitions)
            {
                await Put($"resolver '{key}'", handlers.Handle(new PutResolver(tenantId, key, c.Kind, c.Handles, c.Settings ?? JsonElement.Parse("{}"),
                    c.SecretRef, c.Enabled ?? true) { Provisioned = true }, ct));
            }
            if (problems.Count > 0)
            {
                return changes;
            }

            // What the file no longer lists goes back to the API: disabled, so nothing reaches it until an admin decides, and kept, so deliveries'
            // history still names it.
            foreach (var route in await db.Routes.Where(r => r.TenantId == tenantId && r.ManagedBy == ManagedBy.Configuration).ToListAsync(ct))
            {
                if (!routes.ContainsKey(route.Key))
                {
                    route.ManagedBy = ManagedBy.Api;
                    route.Enabled = false;
                    route.UpdatedAt = now;
                    route.DefinitionHash = ConfigurationRules.RouteHash(route);
                    changes++;
                }
            }
            foreach (var target in await db.IntegrationTargets.Where(t => t.TenantId == tenantId && t.ManagedBy == ManagedBy.Configuration).ToListAsync(ct))
            {
                if (!targetKeys.Contains(target.Key))
                {
                    target.ManagedBy = ManagedBy.Api;
                    target.Enabled = false;
                    target.UpdatedAt = now;
                    target.DefinitionHash = ConfigurationRules.TargetHash(target);
                    changes++;
                }
            }
            foreach (var integration in await db.Integrations.Where(i => i.TenantId == tenantId && i.ManagedBy == ManagedBy.Configuration).ToListAsync(ct))
            {
                if (!integrations.ContainsKey(integration.Key))
                {
                    integration.ManagedBy = ManagedBy.Api;
                    integration.Enabled = false;
                    integration.UpdatedAt = now;
                    integration.DefinitionHash = ConfigurationRules.IntegrationHash(integration);
                    changes++;
                }
            }
            foreach (var resolver in await db.Resolvers.Where(c => c.TenantId == tenantId && c.ManagedBy == ManagedBy.Configuration).ToListAsync(ct))
            {
                if (!resolverDefinitions.ContainsKey(resolver.Key))
                {
                    resolver.ManagedBy = ManagedBy.Api;
                    resolver.Enabled = false;
                    resolver.UpdatedAt = now;
                    resolver.DefinitionHash = ConfigurationRules.ResolverHash(resolver);
                    changes++;
                }
            }
            foreach (var entry in await db.Allowlist.Where(a => a.TenantId == tenantId && a.ManagedBy == ManagedBy.Configuration).ToListAsync(ct))
            {
                if (!destinations.Contains(entry.Destination))
                {
                    db.Allowlist.Remove(entry);
                    changes++;
                }
            }
            await db.SaveChangesAsync(ct);
            return changes;
        }
    }

    public static class ProvisioningRegistration
    {
        /// <summary>
        /// Configuration provisioning (<c>Provisioning:Path</c>). Register after the database migrator, before the stage workers: hosted services
        /// start in order, so the stages start with the configuration in place.
        /// </summary>
        public static IServiceCollection AddConfigurationProvisioning(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            services.Configure<ProvisioningOptions>(configuration.GetSection(ProvisioningOptions.Section));
            services.AddScoped<IProvisioningScope, UnrecordedProvisioningScope>();
            services.AddHostedService<ConfigurationProvisioner>();
            return services;
        }
    }
}
