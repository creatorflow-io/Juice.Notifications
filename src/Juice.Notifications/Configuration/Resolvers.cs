using System.Text.Json;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Configuration
{
    // The resolvers a tenant uses for the people kinds (design §6.1, D20; phase-2-plan M10): which implementation, which recipient kinds it
    // handles for the tenant, and its settings. Saved through the API or provisioned from configuration, like integrations.

    public sealed record ResolverView(string Key, string Kind, IReadOnlyList<string> Handles, JsonElement Settings, string? SecretRef, bool Enabled,
        string ManagedBy, DateTimeOffset UpdatedAt);

    /// <summary>Creates or replaces a tenant's resolver. Its kind (the implementation) is fixed once created.</summary>
    public sealed record PutResolver(Guid NotificationTenantId, string Key, string? Kind, IReadOnlyList<string>? Handles, JsonElement? Settings,
        string? SecretRef, bool? Enabled) : MessageBase, IRequest<Outcome<ResolverView>>
    {
        internal bool Provisioned { get; init; }
    }

    /// <summary>
    /// Deletes a tenant's resolver. Requests accepted naming it and not yet planned skip those recipients, with a warning; disabling it has the
    /// same effect and keeps the row.
    /// </summary>
    public sealed record DeleteResolver(Guid NotificationTenantId, string Key) : MessageBase, IRequest<Outcome<bool>>;

    public static partial class ConfigurationRules
    {
        internal static string ResolverHash(string kind, IEnumerable<string> handles, string settings, string? secretRef, bool enabled)
            => Hash(kind, string.Join(",", handles), Canonical(settings), secretRef, enabled.ToString());

        internal static string ResolverHash(RecipientResolverConfig r) => ResolverHash(r.Kind, r.Handles, r.Settings, r.SecretRef, r.Enabled);
    }

    internal sealed partial class ConfigurationHandlers :
        IRequestHandler<PutResolver, Outcome<ResolverView>>, IRequestHandler<DeleteResolver, Outcome<bool>>
    {
        public async ValueTask<Outcome<ResolverView>> Handle(PutResolver r, CancellationToken ct)
        {
            if (!ConfigurationRules.Key().IsMatch(r.Key))
            {
                return Invalid<ResolverView>(new FieldError("/key", "A key is 1–63 lowercase letters, digits or hyphens, starting with a letter."));
            }
            var existing = await db.Resolvers.SingleOrDefaultAsync(c => c.TenantId == r.NotificationTenantId && c.Key == r.Key, ct);
            if (existing?.ManagedBy == ManagedBy.Configuration && !r.Provisioned)
            {
                return ConfigManaged<ResolverView>("resolver " + r.Key);
            }
            var errors = new List<FieldError>();
            // From configuration the file states the whole resolver, its kind included; through the API a left-out kind is the stored one.
            var kind = r.Provisioned ? r.Kind : r.Kind ?? existing?.Kind;
            var implementation = resolvers.FirstOrDefault(x => x.Configurable && x.Key == kind);
            // A resolver whose implementation was uninstalled can still be disabled through the API (everything else left out or as stored):
            // stopping it can't wait for the package to come back.
            var orphanDisable = implementation is null && existing is not null && kind == existing.Kind && !r.Provisioned && r.Enabled == false
                && r.Handles is null && r.Settings is null && (r.SecretRef is null || r.SecretRef == existing.SecretRef);
            if (implementation is null && !orphanDisable)
            {
                var kinds = resolvers.Where(x => x.Configurable).Select(x => x.Key).Order(StringComparer.Ordinal).ToList();
                errors.Add(new FieldError("/kind", kinds.Count > 0 ? $"The resolver kinds are: {string.Join(", ", kinds)}." : "No resolvers are installed."));
            }
            else if (existing is not null && existing.Kind != kind)
            {
                errors.Add(new FieldError("/kind", $"A resolver's kind is fixed: {r.Key} is {existing.Kind}. Create another resolver."));
            }
            // Left out: through the API, as stored; from configuration, which states the whole resolver, every kind the implementation can.
            var handles = r.Handles is { } sent ? sent.ToList()
                : (r.Provisioned ? null : existing?.Handles.ToList()) ?? implementation?.Kinds.ToList() ?? [];
            // Only against an implementation: without one there is nothing to say a kind is wrong about, and /kind says why.
            if (handles.Count == 0 && implementation is not null)
            {
                errors.Add(new FieldError("/handles", "A resolver handles at least one recipient kind."));
            }
            for (var i = 0; i < handles.Count; i++)
            {
                if (implementation is not null && (handles[i] is null || !implementation.Kinds.Contains(handles[i])))
                {
                    errors.Add(new FieldError($"/handles/{i}", $"A {kind} resolver handles: {string.Join(", ", implementation.Kinds.Order(StringComparer.Ordinal))}."));
                }
            }
            handles = handles.Where(h => h is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (r.Settings is { } settings && settings.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new FieldError("/settings", "Settings are a JSON object."));
            }
            else if (r.Settings is { } withNul && CanonicalJson.FirstNul(withNul, "/settings") is { } nul)
            {
                errors.Add(new FieldError(nul, RequestRules.NulMessage));
            }
            else if (r.Settings is { } withDuplicate && CanonicalJson.FirstDuplicate(withDuplicate, "/settings") is { } duplicate)
            {
                errors.Add(new FieldError(duplicate, "This object repeats a property name; each name appears once."));
            }
            // As for integrations: a secret sent back as stored is no new secret; from configuration the file states it.
            var sentSecret = !r.Provisioned && existing is not null && r.SecretRef == existing.SecretRef ? null : r.SecretRef;
            var secretRef = r.Provisioned ? r.SecretRef : sentSecret ?? existing?.SecretRef;
            var enabled = r.Enabled ?? existing?.Enabled ?? true;
            var settingsJson = r.Settings is { ValueKind: JsonValueKind.Object } s ? CanonicalJson.Write(s) : existing?.Settings ?? "{}";
            var sameSettings = existing is not null && ConfigurationRules.Canonical(settingsJson) == ConfigurationRules.Canonical(existing.Settings);
            var disablesOnly = !r.Provisioned && existing is not null && !enabled && sameSettings && secretRef == existing.SecretRef
                && handles.SequenceEqual(existing.Handles);
            if (!ConfigurationRules.IsSecretRef(sentSecret, Delivery, r.Provisioned))
            {
                errors.Add(new FieldError("/secretRef", ConfigurationRules.SecretRefRule("A secret", Delivery, r.Provisioned)));
            }
            else if (!r.Provisioned && !disablesOnly
                && ConfigurationRules.SecretOutOfReach(Delivery, "/secretRef", secretRef, "The stored secret", ConfigurationRules.SecretRefRule("name one: it", Delivery, false)) is { } outOfReach)
            {
                errors.Add(outOfReach);
            }
            if (implementation is not null && errors.Count == 0 && (existing is null || !sameSettings) && implementation.CheckSettings(settingsJson) is { } problem)
            {
                errors.Add(new FieldError("/settings", problem));
            }
            if (errors.Count > 0)
            {
                return Invalid<ResolverView>([.. errors]);
            }
            var now = clock.UtcNow();
            var managedBy = r.Provisioned ? ManagedBy.Configuration : existing?.ManagedBy ?? ManagedBy.Api;
            var hash = ConfigurationRules.ResolverHash(kind!, handles, settingsJson, secretRef, enabled);
            if (existing is null)
            {
                existing = new RecipientResolverConfig
                {
                    TenantId = r.NotificationTenantId, Key = r.Key, Kind = kind!, Handles = [.. handles], Settings = settingsJson, SecretRef = secretRef,
                    Enabled = enabled, ManagedBy = managedBy, DefinitionHash = hash, UpdatedAt = now,
                };
                db.Resolvers.Add(existing);
            }
            else if ((existing.DefinitionHash, existing.ManagedBy) != (hash, managedBy))
            {
                existing.Handles = [.. handles];
                existing.Settings = settingsJson;
                existing.SecretRef = secretRef;
                existing.Enabled = enabled;
                existing.ManagedBy = managedBy;
                existing.UpdatedAt = now;
                existing.DefinitionHash = hash;
            }
            return Outcome<ResolverView>.Ok(ConfigurationQueries.Resolver(existing));
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteResolver r, CancellationToken ct)
        {
            var existing = await db.Resolvers.SingleOrDefaultAsync(c => c.TenantId == r.NotificationTenantId && c.Key == r.Key, ct);
            if (existing is null)
            {
                return Outcome<bool>.Ok(false);
            }
            if (existing.ManagedBy == ManagedBy.Configuration)
            {
                return ConfigManaged<bool>("resolver " + r.Key);
            }
            db.Resolvers.Remove(existing);
            return Outcome<bool>.Ok(true);
        }
    }

    public static partial class ConfigurationQueries
    {
        internal static ResolverView Resolver(RecipientResolverConfig c)
            => new(c.Key, c.Kind, c.Handles, JsonElement.Parse(c.Settings), c.SecretRef, c.Enabled, c.ManagedBy, c.UpdatedAt);
    }

    public sealed partial class ConfigurationReader
    {
        public async Task<IReadOnlyList<ResolverView>> ResolversAsync(Guid tenantId, CancellationToken ct)
            => (await db.Resolvers.AsNoTracking().Where(c => c.TenantId == tenantId).OrderBy(c => c.Key).ToListAsync(ct)).Select(ConfigurationQueries.Resolver).ToList();
    }
}
