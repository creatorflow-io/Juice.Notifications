using System.Text.Json;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Configuration
{
    // A tenant's escalation policies (design §7.1, §12; phase-3-plan I34): who else to tell, and after how long, while a correlation stays open.
    // Through the API like routes and resolvers, audited.

    public sealed record EscalationPolicyView(string Key, JsonElement Steps, bool StopOnAcknowledge, string ManagedBy, DateTimeOffset UpdatedAt);

    /// <summary>One step: after how many minutes (from the escalation's start, or the step before), and the recipients it adds.</summary>
    public sealed record EscalationStepSpec(int AfterMinutes, IReadOnlyList<RecipientSpec> Recipients);

    public sealed record PutEscalationPolicy(Guid NotificationTenantId, string Key, JsonElement? Steps, bool? StopOnAcknowledge)
        : MessageBase, ITenantCommand, IRequest<Outcome<EscalationPolicyView>>, IOwnedSetupWrite
    {
        public string? ManagedBy { get; init; }
    }

    /// <summary>Deletes a policy; an escalation running it stops at its next step.</summary>
    public sealed record DeleteEscalationPolicy(Guid NotificationTenantId, string Key) : MessageBase, ITenantCommand, IRequest<Outcome<bool>>, IOwnedSetupWrite
    {
        public string? ManagedBy { get; init; }
    }

    public static class EscalationRules
    {
        public const int MaxSteps = 10;
        public const int MaxRecipients = 20;
        public const int MaxAfterMinutes = 7 * 24 * 60;

        /// <summary>The recipient kinds a step names: a route match, or people through one of the tenant's resolvers.</summary>
        private static readonly string[] Kinds = [RecipientKinds.Route, .. RecipientKinds.People];

        /// <summary>
        /// Parses a policy's steps (pointers under <c>/steps</c>): 1–10 steps of <c>{ afterMinutes, recipients }</c>, each naming 1–20 recipients
        /// as a request would (<c>{ \"kind\": \"group\", \"id\": \"sre\", \"resolver\": \"staff\" }</c>, or <c>{ \"kind\": \"route\" }</c>). A people
        /// kind names the tenant's resolver that handles it, enabled now (<paramref name="resolvers"/>; null skips that check, as when reading).
        /// </summary>
        public static (IReadOnlyList<EscalationStepSpec>? Steps, IReadOnlyList<FieldError> Errors) Parse(JsonElement steps,
            IReadOnlyList<RecipientResolverConfig>? resolvers)
        {
            if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is 0 or > MaxSteps)
            {
                return (null, [new FieldError("/steps", $"A policy has 1–{MaxSteps} steps: {{ \"afterMinutes\": 15, \"recipients\": [ … ] }}.")]);
            }
            var errors = new List<FieldError>();
            var parsed = new List<EscalationStepSpec>();
            var i = 0;
            foreach (var step in steps.EnumerateArray())
            {
                var at = $"/steps/{i++}";
                if (step.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new FieldError(at, "A step is { \"afterMinutes\": 15, \"recipients\": [ … ] }."));
                    continue;
                }
                foreach (var property in step.EnumerateObject().Where(p => p.Name is not ("afterMinutes" or "recipients")))
                {
                    errors.Add(new FieldError($"{at}/{Types.TypeRules.Pointer(property.Name)}", "A step has afterMinutes and recipients."));
                }
                var after = step.TryGetProperty("afterMinutes", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var m) && m is >= 0 and <= MaxAfterMinutes
                    ? m : (int?)null;
                if (after is null)
                {
                    errors.Add(new FieldError(at + "/afterMinutes", $"A step waits 0–{MaxAfterMinutes} minutes (7 days) after the one before."));
                }
                if (!step.TryGetProperty("recipients", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaxRecipients)
                {
                    errors.Add(new FieldError(at + "/recipients", $"A step adds 1–{MaxRecipients} recipients."));
                    continue;
                }
                var specs = new List<RecipientSpec>();
                var j = 0;
                foreach (var r in list.EnumerateArray())
                {
                    var rat = $"{at}/recipients/{j++}";
                    if (Recipient(r, rat, resolvers, errors) is { } spec)
                    {
                        specs.Add(spec);
                    }
                }
                if (after is { } wait)
                {
                    parsed.Add(new EscalationStepSpec(wait, specs));
                }
            }
            return errors.Count > 0 ? (null, errors) : (parsed, []);
        }

        /// <summary>A stored policy's steps (checked when saved).</summary>
        public static IReadOnlyList<EscalationStepSpec> Read(string json)
        {
            using var document = JsonDocument.Parse(json);
            var (steps, errors) = Parse(document.RootElement, null);
            return steps ?? throw new InvalidOperationException($"A stored escalation policy doesn't parse: {string.Join("; ", errors.Select(e => e.Pointer + ": " + e.Message))}");
        }

        private static RecipientSpec? Recipient(JsonElement r, string at, IReadOnlyList<RecipientResolverConfig>? resolvers, List<FieldError> errors)
        {
            string? Text(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (r.ValueKind != JsonValueKind.Object || Text("kind") is not { } kind || !Kinds.Contains(kind))
            {
                errors.Add(new FieldError(at + "/kind", "A recipient's kind is route, user, group, role or topic."));
                return null;
            }
            var allowed = kind switch
            {
                // A step's route is every route the notification matches, or one route by its key (decision G5), whatever its match.
                RecipientKinds.Route => new[] { "kind", "key" },
                RecipientKinds.Topic => ["kind", "key", "resolver"],
                _ => ["kind", "id", "resolver"],
            };
            foreach (var property in r.EnumerateObject().Where(p => !allowed.Contains(p.Name)))
            {
                errors.Add(new FieldError($"{at}/{Types.TypeRules.Pointer(property.Name)}", $"A {kind} recipient has {string.Join(", ", allowed)}."));
            }
            if (kind == RecipientKinds.Route)
            {
                if (!r.TryGetProperty("key", out var routeKey))
                {
                    return new RecipientSpec(kind);
                }
                if (routeKey.ValueKind != JsonValueKind.String || !ConfigurationRules.Key().IsMatch(routeKey.GetString()!))
                {
                    errors.Add(new FieldError(at + "/key", "A route's key: 1–63 lowercase letters, digits or hyphens, starting with a letter."));
                    return null;
                }
                return new RecipientSpec(kind, Key: routeKey.GetString());
            }
            var resolver = Text("resolver");
            if (!ConfigurationRules.Key().IsMatch(resolver ?? ""))
            {
                errors.Add(new FieldError(at + "/resolver", "Name the tenant's resolver for these people: its key."));
                return null;
            }
            if (resolvers is not null && !resolvers.Any(c => c.Key == resolver && c.Enabled && c.Handles.Contains(kind)))
            {
                errors.Add(new FieldError(at + "/resolver", $"The tenant has no enabled resolver {resolver} that handles {kind}."));
                return null;
            }
            if (kind == RecipientKinds.Topic)
            {
                var key = Text("key");
                if (!RequestRules.IsKey(key))
                {
                    errors.Add(new FieldError(at + "/key", "A topic's key is 1–200 printable characters without spaces."));
                    return null;
                }
                return new RecipientSpec(kind, Key: key, Resolver: resolver);
            }
            var id = Text("id");
            if (!RequestRules.IsRecipientId(id))
            {
                errors.Add(new FieldError(at + "/id", $"An id is 1–{RequestRules.MaxRecipientIdLength} characters, no control characters."));
                return null;
            }
            return new RecipientSpec(kind, Id: id, Resolver: resolver);
        }
    }

    public static partial class ConfigurationRules
    {
        internal static string EscalationPolicyHash(string steps, bool stopOnAcknowledge) => Hash(Canonical(steps), stopOnAcknowledge.ToString());
    }

    internal sealed partial class ConfigurationHandlers :
        IRequestHandler<PutEscalationPolicy, Outcome<EscalationPolicyView>>, IRequestHandler<DeleteEscalationPolicy, Outcome<bool>>
    {
        public async ValueTask<Outcome<EscalationPolicyView>> Handle(PutEscalationPolicy r, CancellationToken ct)
        {
            if (!ConfigurationRules.Key().IsMatch(r.Key))
            {
                return Invalid<EscalationPolicyView>(new FieldError("/key", "A key is 1–63 lowercase letters, digits or hyphens, starting with a letter."));
            }
            var existing = await db.EscalationPolicies.SingleOrDefaultAsync(p => p.TenantId == r.NotificationTenantId && p.Key == r.Key, ct);
            if (Owned<EscalationPolicyView>(existing?.ManagedBy, r.ManagedBy, "escalation policy " + r.Key) is { } owned)
            {
                return owned;
            }
            string steps;
            if (r.Steps is { } sent)
            {
                if (CanonicalJson.Unstorable(sent, "/steps") is { } unstorable)
                {
                    return Invalid<EscalationPolicyView>(unstorable);
                }
                var configs = await db.Resolvers.AsNoTracking().Where(c => c.TenantId == r.NotificationTenantId).ToListAsync(ct);
                var (_, errors) = EscalationRules.Parse(sent, configs);
                if (errors.Count > 0)
                {
                    return Invalid<EscalationPolicyView>([.. errors]);
                }
                steps = CanonicalJson.Write(sent);
            }
            else if (existing is not null)
            {
                steps = existing.Steps;
            }
            else
            {
                return Invalid<EscalationPolicyView>(new FieldError("/steps", "A new policy needs its steps."));
            }
            var stop = r.StopOnAcknowledge ?? existing?.StopOnAcknowledge ?? true;
            var hash = ConfigurationRules.EscalationPolicyHash(steps, stop);
            var now = clock.GetUtcNow();
            if (existing is null)
            {
                existing = new EscalationPolicy
                {
                    TenantId = r.NotificationTenantId, Key = r.Key, Steps = steps, StopOnAcknowledge = stop, DefinitionHash = hash, UpdatedAt = now, ManagedBy = Caller(r.ManagedBy),
                };
                db.EscalationPolicies.Add(existing);
            }
            else if (existing.DefinitionHash != hash)
            {
                (existing.Steps, existing.StopOnAcknowledge, existing.DefinitionHash, existing.UpdatedAt) = (steps, stop, hash, now);
            }
            return Outcome<EscalationPolicyView>.Ok(ConfigurationQueries.EscalationPolicy(existing));
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteEscalationPolicy r, CancellationToken ct)
        {
            var existing = await db.EscalationPolicies.SingleOrDefaultAsync(p => p.TenantId == r.NotificationTenantId && p.Key == r.Key, ct);
            if (existing is null)
            {
                return Outcome<bool>.Ok(false);
            }
            if (Owned<bool>(existing.ManagedBy, r.ManagedBy, "escalation policy " + r.Key) is { } owned)
            {
                return owned;
            }
            db.EscalationPolicies.Remove(existing);
            return Outcome<bool>.Ok(true);
        }
    }

    public static partial class ConfigurationQueries
    {
        internal static EscalationPolicyView EscalationPolicy(EscalationPolicy p)
            => new(p.Key, JsonElement.Parse(p.Steps), p.StopOnAcknowledge, p.ManagedBy, p.UpdatedAt);
    }

    public sealed partial class ConfigurationReader
    {
        public async Task<IReadOnlyList<EscalationPolicyView>> EscalationPoliciesAsync(Guid tenantId, CancellationToken ct)
            => (await db.EscalationPolicies.AsNoTracking().Where(p => p.TenantId == tenantId).OrderBy(p => p.Key).ToListAsync(ct))
                .Select(ConfigurationQueries.EscalationPolicy).ToList();
    }
}
