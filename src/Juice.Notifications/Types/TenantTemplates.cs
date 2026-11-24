using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Juice.Notifications.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Types
{
    // A tenant's own templates (design D22, phase-4-plan I45): an override of the application's template for a type's latest version and a
    // channel, written by the tenant's admins, checked as declarations are, and rendered first. Never a new type version: the producer owns those.

    public sealed record TenantTypeChannel(string Channel, bool Declared, bool Overridden);

    /// <summary>A type the tenant receives (its application is linked to the tenant), at its latest version, and its channels' templates.</summary>
    public sealed record TenantTypeView(string Key, string Name, int Version, string Application, IReadOnlyList<TenantTypeChannel> Channels);

    /// <summary>One channel's templates of a type version: the application's, and the tenant's override if any (with its version and time).</summary>
    public sealed record TenantTemplateView(string Type, int TypeVersion, string Channel, string Locale, string? Application, string? Tenant,
        int? TenantVersion, DateTimeOffset? UpdatedAt);

    /// <summary>What a template renders to, as the channel stores it (email: subject, text, html), or why it doesn't.</summary>
    public sealed record PreviewView(string Channel, JsonElement? Content, string? Error, JsonElement Data);

    /// <summary>A locale's templates on a channel: whether the application declares one and the tenant has its own.</summary>
    public sealed record TenantTypeLocale(string Locale, bool Declared, bool Overridden);

    public sealed record TenantTypeChannelDetail(string Channel, bool Declared, bool Overridden, IReadOnlyList<TenantTypeLocale> Locales);

    /// <summary>
    /// A type the tenant receives, for its admins (phase-4c-plan M39): its latest version as declared, every version's hash and time, where
    /// its inbox items go in this tenant (<c>Group</c>; <c>GroupChosen</c> when the tenant chose it, otherwise the type's own), and its
    /// channels' templates by locale. <c>ServiceType</c>: the service's own, whose templates stay as they are.
    /// </summary>
    public sealed record TenantTypeDetail(
        string Key, string Application, int Version, string Name, string Category, string DisplayGroup, string Group, bool GroupChosen,
        NotificationPriority Priority, bool Mandatory, bool ExternalRecipients, string Correlation, JsonElement Schema, bool ServiceType,
        IReadOnlyList<TypeVersionView> Versions, IReadOnlyList<TenantTypeChannelDetail> Channels);

    public sealed record PutTenantTemplate(Guid NotificationTenantId, string TypeKey, string Channel, string? Locale, string? Body)
        : MessageBase, IRequest<Outcome<TenantTemplateView>>;

    public sealed record DeleteTenantTemplate(Guid NotificationTenantId, string TypeKey, string Channel, string? Locale) : MessageBase, IRequest<Outcome<bool>>;

    public static class TenantTemplateRules
    {
        /// <summary>The latest version of a type the tenant receives: its application linked to the tenant; null otherwise.</summary>
        internal static async Task<(NotificationType Type, string Application)?> TypeAsync(NotificationsDbContext db, Guid tenantId, string typeKey, CancellationToken ct)
        {
            var found = await (from type in db.Types
                               join app in db.Applications on type.ApplicationId equals app.Id
                               where type.Key == typeKey && db.ApplicationTenants.Any(l => l.ApplicationId == app.Id && l.TenantId == tenantId)
                               orderby type.Version descending
                               select new { Type = type, app.Key }).AsNoTracking().FirstOrDefaultAsync(ct);
            return found is null ? null : (found.Type, found.Key);
        }

        /// <summary>
        /// Data shaped by the type's schema, for a preview with none given: each property's first example or default, else a value of its type
        /// (a string is its name, a number 1); objects and arrays filled one level at a time, at most 5 deep.
        /// </summary>
        public static JsonNode? Sample(JsonElement schema, string name = "value", int depth = 0)
        {
            if (schema.ValueKind != JsonValueKind.Object || depth > 5)
            {
                return null;
            }
            if (schema.TryGetProperty("examples", out var examples) && examples.ValueKind == JsonValueKind.Array && examples.GetArrayLength() > 0)
            {
                return JsonNode.Parse(examples[0].GetRawText());
            }
            foreach (var key in new[] { "default", "const" })
            {
                if (schema.TryGetProperty(key, out var value))
                {
                    return JsonNode.Parse(value.GetRawText());
                }
            }
            if (schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                return JsonNode.Parse(choices[0].GetRawText());
            }
            var type = schema.TryGetProperty("type", out var t) ? t.ValueKind == JsonValueKind.Array ? t[0].GetString() : t.GetString() : null;
            if (type is null && schema.TryGetProperty("properties", out _))
            {
                type = "object";
            }
            switch (type)
            {
                case "object":
                    var o = new JsonObject();
                    if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in properties.EnumerateObject())
                        {
                            o[p.Name] = Sample(p.Value, p.Name, depth + 1);
                        }
                    }
                    return o;
                case "array":
                    return schema.TryGetProperty("items", out var items) ? new JsonArray(Sample(items, name, depth + 1)) : new JsonArray();
                case "integer" or "number":
                    return JsonValue.Create(1);
                case "boolean":
                    return JsonValue.Create(true);
                case "string":
                    return JsonValue.Create(name);
                default:
                    return null;
            }
        }

        /// <summary>The template's output as the channel stores it, the same steps as the delivery planner's.</summary>
        internal static async Task<string> RenderAsync(ChannelDefinition channel, NotificationType type, string body, JsonElement data, CancellationToken ct)
        {
            var links = new Dictionary<string, string> { ["self"] = "https://example.com/" + type.Key };
            var model = new RenderModel(type.Key, type.Version, type.Name, type.Priority, data, new Dictionary<string, string>(), links);
            var rendered = await TemplateRenderer.RenderAsync(body, model, ct);
            return channel.Content switch
            {
                ContentShape.Message => ChannelContent.Email(rendered, type.Name),
                ContentShape.Item => ChannelContent.Inbox(rendered, type.Key, type.Name, links, type.Actions),
                ContentShape.Chat => ChannelContent.Chat(rendered, type.Name, links, type.Actions),
                ContentShape.Text => ChannelContent.Text(rendered),
                _ => ChannelContent.Webhook(rendered, ChannelContent.IsJson(rendered)),
            };
        }

        /// <summary>Why a body can't be a tenant's template on the channel (as a declaration's would be refused), or null.</summary>
        internal static string? Problem(ChannelDefinition channel, string? body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return "A template has a body.";
            }
            if (body.Contains('\0'))
            {
                return Intake.RequestRules.NulMessage;
            }
            if (body == TemplateEngines.Passthrough)
            {
                return channel.Passthrough ? null : $"The {channel.Key} channel needs a template; only channels that send data as is accept \"passthrough\".";
            }
            if (System.Text.Encoding.UTF8.GetByteCount(body) > TypeRules.MaxTemplateBytes)
            {
                return $"A template is at most {TypeRules.MaxTemplateBytes / 1024} KB.";
            }
            return TemplateRenderer.Check(body);
        }
    }

    public sealed class TenantTemplateReader(NotificationsDbContext db, IChannelRegistry channels)
    {
        public async Task<IReadOnlyList<TenantTypeView>> TypesAsync(Guid tenantId, CancellationToken ct)
        {
            var latest = await (from type in db.Types
                                join app in db.Applications on type.ApplicationId equals app.Id
                                where db.ApplicationTenants.Any(l => l.ApplicationId == app.Id && l.TenantId == tenantId)
                                      && !db.Types.Any(newer => newer.ApplicationId == type.ApplicationId && newer.Key == type.Key && newer.Version > type.Version)
                                orderby type.Key
                                select new { type.Id, type.Key, type.Name, type.Version, Application = app.Key }).AsNoTracking().ToListAsync(ct);
            var templates = await TemplatesOfAsync(tenantId, latest.Select(t => t.Id).ToList(), ct);
            return latest.Select(t => new TenantTypeView(t.Key, t.Name, t.Version, t.Application,
                ChannelsOf(templates, t.Id).Select(c => new TenantTypeChannel(c.Channel, c.Declared, c.Overridden)).ToList())).ToList();
        }

        /// <summary>Which templates the types have here, the application's and the tenant's own, by channel and locale.</summary>
        private async Task<List<TemplateKey>> TemplatesOfAsync(Guid tenantId, List<Guid> typeIds, CancellationToken ct)
            => (await db.Templates.AsNoTracking().Where(t => typeIds.Contains(t.TypeId) && (t.TenantId == null || t.TenantId == tenantId))
                .Select(t => new { t.TypeId, t.Channel, t.Locale, Tenant = t.TenantId != null }).Distinct().ToListAsync(ct))
                .Select(t => new TemplateKey(t.TypeId, t.Channel, t.Locale, t.Tenant)).ToList();

        /// <summary>
        /// Every channel of the deployment for one type: whether the application declares a template on it, whether the tenant has its own,
        /// and the same per locale. The types list and a type's page both read it here, so they never disagree.
        /// </summary>
        private List<TenantTypeChannelDetail> ChannelsOf(List<TemplateKey> templates, Guid typeId) => channels.All.Select(c =>
        {
            var here = templates.Where(t => t.TypeId == typeId && t.Channel == c.Key).ToList();
            var locales = here.Select(t => t.Locale).Distinct().Order(StringComparer.Ordinal)
                .Select(l => new TenantTypeLocale(l, here.Any(t => t.Locale == l && !t.Tenant), here.Any(t => t.Locale == l && t.Tenant))).ToList();
            return new TenantTypeChannelDetail(c.Key, here.Any(t => !t.Tenant), here.Any(t => t.Tenant), locales);
        }).OrderBy(c => c.Channel, StringComparer.Ordinal).ToList();

        private sealed record TemplateKey(Guid TypeId, string Channel, string Locale, bool Tenant);

        public async Task<Outcome<TenantTypeDetail>> DetailAsync(Guid tenantId, string typeKey, CancellationToken ct)
        {
            if (await TenantTemplateRules.TypeAsync(db, tenantId, typeKey, ct) is not { } found)
            {
                return Outcome<TenantTypeDetail>.NotFound($"This tenant receives no type '{typeKey}'.");
            }
            var type = found.Type;
            var versions = (await db.Types.AsNoTracking().Where(t => t.ApplicationId == type.ApplicationId && t.Key == type.Key).OrderBy(t => t.Version)
                .ToListAsync(ct)).Select(t => TypeQueries.View(t, isNew: false)).ToList();
            var place = await Inbox.DisplayGroupCatalog.PlaceAsync(db, tenantId, type.Key, ct);
            var channelDetails = ChannelsOf(await TemplatesOfAsync(tenantId, [type.Id], ct), type.Id);
            return Outcome<TenantTypeDetail>.Ok(new TenantTypeDetail(type.Key, found.Application, type.Version, type.Name, type.Category, type.DisplayGroup,
                place.Group, place.Chosen, type.Priority, type.Mandatory, type.ExternalRecipients, type.Correlation,
                JsonElement.Parse(type.Schema), People.ServiceTypes.IsServiceType(type.Key), versions, channelDetails));
        }

        public async Task<Outcome<TenantTemplateView>> GetAsync(Guid tenantId, string typeKey, string channel, string? locale, CancellationToken ct)
        {
            if (await TenantTemplateRules.TypeAsync(db, tenantId, typeKey, ct) is not { } found)
            {
                return Outcome<TenantTemplateView>.NotFound($"This tenant receives no type '{typeKey}'.");
            }
            return Outcome<TenantTemplateView>.Ok(await ViewAsync(db, tenantId, found.Type, channel, locale ?? TypeRules.DefaultLocale, ct));
        }

        internal static async Task<TenantTemplateView> ViewAsync(NotificationsDbContext db, Guid tenantId, NotificationType type, string channel, string locale,
            CancellationToken ct)
        {
            var rows = await db.Templates.AsNoTracking().Where(t => t.TypeId == type.Id && t.Channel == channel && t.Locale == locale
                && (t.TenantId == null || t.TenantId == tenantId)).OrderByDescending(t => t.Version).ToListAsync(ct);
            var app = rows.FirstOrDefault(t => t.TenantId == null);
            var own = rows.FirstOrDefault(t => t.TenantId != null);
            string? Body(Template? t) => t is null ? null : t.Engine == TemplateEngines.Passthrough ? TemplateEngines.Passthrough : t.Body;
            return new TenantTemplateView(type.Key, type.Version, channel, locale, Body(app), Body(own), own?.Version, own?.CreatedAt);
        }

        /// <summary>
        /// Renders a template, or the one in effect for the tenant when none is given, with the data given, or a sample from the type's schema when
        /// none is. Nothing is stored or sent; a template that fails says why, as a delivery's would.
        /// </summary>
        public async Task<Outcome<PreviewView>> PreviewAsync(Guid tenantId, string? typeKey, string? channelKey, string? locale, string? body, JsonElement? data,
            CancellationToken ct)
        {
            if (typeKey is null || await TenantTemplateRules.TypeAsync(db, tenantId, typeKey, ct) is not { } found)
            {
                return Outcome<PreviewView>.NotFound($"This tenant receives no type '{typeKey}'.");
            }
            if (channelKey is null || channels.Find(channelKey) is not { } channel)
            {
                return Outcome<PreviewView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/channel", $"One of: {string.Join(", ", channels.All.Select(c => c.Key).Order())}.")]);
            }
            var sample = data is { ValueKind: JsonValueKind.Object } given ? given
                : JsonSerializer.SerializeToElement(TenantTemplateRules.Sample(JsonElement.Parse(found.Type.Schema)) ?? new JsonObject());
            if (body is null)
            {
                // The template in effect in the locale asked for (the default one when none is).
                var effective = await ViewAsync(db, tenantId, found.Type, channel.Key, locale ?? TypeRules.DefaultLocale, ct);
                body = effective.Tenant ?? effective.Application;
                if (body is null)
                {
                    return Outcome<PreviewView>.Ok(new PreviewView(channel.Key, null,
                        locale is null || locale == TypeRules.DefaultLocale ? $"The type has no {channel.Key} template." : $"The type has no {channel.Key} template in {locale}.", sample));
                }
            }
            if (TenantTemplateRules.Problem(channel, body) is { } problem)
            {
                return Outcome<PreviewView>.Ok(new PreviewView(channel.Key, null, problem, sample));
            }
            if (body == TemplateEngines.Passthrough)
            {
                return Outcome<PreviewView>.Ok(new PreviewView(channel.Key, JsonSerializer.SerializeToElement(new { passthrough = true, data = sample }), null, sample));
            }
            try
            {
                var content = await TenantTemplateRules.RenderAsync(channel, found.Type, body, sample, ct);
                return Outcome<PreviewView>.Ok(new PreviewView(channel.Key, JsonElement.Parse(content), null, sample));
            }
            catch (RenderException ex)
            {
                return Outcome<PreviewView>.Ok(new PreviewView(channel.Key, null, ex.Message, sample));
            }
        }
    }

    internal sealed class TenantTemplateHandlers(NotificationsDbContext db, IChannelRegistry channels, TimeProvider clock)
        : IRequestHandler<PutTenantTemplate, Outcome<TenantTemplateView>>, IRequestHandler<DeleteTenantTemplate, Outcome<bool>>
    {
        public async ValueTask<Outcome<TenantTemplateView>> Handle(PutTenantTemplate r, CancellationToken ct)
        {
            if (await TenantTemplateRules.TypeAsync(db, r.NotificationTenantId, r.TypeKey, ct) is not { } found)
            {
                return Outcome<TenantTemplateView>.NotFound($"This tenant receives no type '{r.TypeKey}'.");
            }
            if (People.ServiceTypes.IsServiceType(found.Type.Key))
            {
                return Outcome<TenantTemplateView>.Conflict(ErrorCodes.ManagedByService, "The service's own types keep their templates.");
            }
            if (channels.Find(r.Channel) is not { } channel)
            {
                return Outcome<TenantTemplateView>.NotFound($"There is no channel '{r.Channel}'.");
            }
            var locale = (r.Locale ?? TypeRules.DefaultLocale).ToLowerInvariant();
            if (locale != TypeRules.DefaultLocale && !await db.Templates.AnyAsync(t => t.TypeId == found.Type.Id && t.Channel == channel.Key && t.Locale == locale, ct))
            {
                return Outcome<TenantTemplateView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/locale", "A tenant overrides the locales the type declares; deliveries render en today.")]);
            }
            if (TenantTemplateRules.Problem(channel, r.Body) is { } problem)
            {
                return Outcome<TenantTemplateView>.Invalid(ErrorCodes.InvalidRequest, "The template has errors.", [new FieldError("/body", problem)]);
            }
            var version = await db.Templates.Where(t => t.TypeId == found.Type.Id && t.Channel == channel.Key && t.Locale == locale && t.TenantId == r.NotificationTenantId)
                .MaxAsync(t => (int?)t.Version, ct) ?? 0;
            var now = clock.UtcNow();
            var passthrough = r.Body == TemplateEngines.Passthrough;
            db.Templates.Add(new Template
            {
                TenantId = r.NotificationTenantId, TypeId = found.Type.Id, Channel = channel.Key, Locale = locale, Version = version + 1, CreatedAt = now,
                Engine = passthrough ? TemplateEngines.Passthrough : TemplateEngines.Scriban, Body = passthrough ? "" : r.Body!,
            });
            var view = await TenantTemplateReader.ViewAsync(db, r.NotificationTenantId, found.Type, channel.Key, locale, ct);
            return Outcome<TenantTemplateView>.Ok(view with { Tenant = r.Body, TenantVersion = version + 1, UpdatedAt = now });
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteTenantTemplate r, CancellationToken ct)
        {
            if (await TenantTemplateRules.TypeAsync(db, r.NotificationTenantId, r.TypeKey, ct) is not { } found)
            {
                return Outcome<bool>.NotFound($"This tenant receives no type '{r.TypeKey}'.");
            }
            var locale = (r.Locale ?? TypeRules.DefaultLocale).ToLowerInvariant();
            // Every version of the tenant's override goes: the application's template is in effect again.
            var removed = await db.Templates.Where(t => t.TypeId == found.Type.Id && t.Channel == r.Channel && t.Locale == locale && t.TenantId == r.NotificationTenantId)
                .ExecuteDeleteAsync(ct);
            return removed > 0 ? Outcome<bool>.Ok(true) : Outcome<bool>.NotFound("The tenant has no template of its own here.");
        }
    }
}
