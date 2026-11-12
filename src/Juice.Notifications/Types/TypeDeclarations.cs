using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Types
{
    /// <summary>
    /// A type declaration as sent (design §5.1). Fields are loose on purpose, so every problem is reported against its field instead of failing to
    /// bind. <see cref="Templates"/> maps a channel to a body (the <c>en</c> locale) or to an object of locale → body.
    /// </summary>
    public sealed record TypeDeclaration(
        string? Key, string? Name, string? Category, string? DisplayGroup, string? Priority, bool? Mandatory, bool? ExternalRecipients,
        JsonElement? Schema, string? Correlation, IReadOnlyList<string>? Redact, JsonElement? Workflow, JsonElement? Templates);

    /// <summary>Declares a version of a type for <paramref name="ApplicationKey"/>: the same declaration as the latest returns it; anything else adds one.</summary>
    public sealed record DeclareType(Guid ApplicationId, string ApplicationKey, TypeDeclaration Declaration) : MessageBase, IRequest<Outcome<TypeVersionView>>;

    /// <summary>A type version: <see cref="IsNew"/> when this declaration created it.</summary>
    public sealed record TypeVersionView(string Key, int Version, string Name, string SchemaHash, string DeclarationHash, DateTimeOffset CreatedAt, bool IsNew);

    /// <summary>A type version in full, as declared.</summary>
    public sealed record TypeVersionDetail(
        string Key, int Version, string Name, string Category, string DisplayGroup, NotificationPriority Priority, bool Mandatory, bool ExternalRecipients,
        string Correlation, IReadOnlyList<string> Redact, JsonElement Schema, JsonElement Workflow,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Templates, string SchemaHash, DateTimeOffset CreatedAt);

    /// <summary>A declaration that passed the rules, with every default filled in.</summary>
    internal sealed record CheckedType(
        string Key, string Name, string Category, string DisplayGroup, NotificationPriority Priority, bool Mandatory, bool ExternalRecipients,
        string Correlation, IReadOnlyList<string> Redact, JsonElement Schema, string Workflow,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Templates)
    {
        /// <summary>Where each (channel, stored locale) template was in the declaration, as sent: the pointer for errors found later.</summary>
        public IReadOnlyDictionary<(string Channel, string Locale), string> Pointers { get; init; } = new Dictionary<(string, string), string>();

        /// <summary>A stored type version as a checked declaration, to hash it as declarations are hashed today.</summary>
        public static CheckedType From(NotificationType type, IEnumerable<Template> templates)
        {
            using var schema = JsonDocument.Parse(type.Schema);
            return new CheckedType(type.Key, type.Name, type.Category, type.DisplayGroup, type.Priority, type.Mandatory, type.ExternalRecipients,
                type.Correlation, type.Redact, schema.RootElement.Clone(), type.Workflow,
                // A template can have later versions (edited after the declaration); the latest of each is the current one.
                templates.GroupBy(t => t.Channel).ToDictionary(g => g.Key,
                    g => (IReadOnlyDictionary<string, string>)g.GroupBy(t => t.Locale).Select(l => l.MaxBy(t => t.Version)!)
                        .ToDictionary(t => t.Locale, t => t.Engine == TemplateEngines.Passthrough ? TypeRules.Passthrough : t.Body)));
        }

        /// <summary>The whole declaration, canonical: equal declarations, however written, hash alike.</summary>
        public string Hash()
        {
            var templates = new JsonObject();
            foreach (var (channel, locales) in Templates.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                var byLocale = new JsonObject();
                foreach (var (locale, body) in locales.OrderBy(l => l.Key, StringComparer.Ordinal))
                {
                    byLocale[locale] = body;
                }
                templates[channel] = byLocale;
            }
            var node = new JsonObject
            {
                ["key"] = Key, ["name"] = Name, ["category"] = Category, ["displayGroup"] = DisplayGroup, ["priority"] = Priority.ToString(),
                ["mandatory"] = Mandatory, ["externalRecipients"] = ExternalRecipients, ["correlation"] = Correlation,
                ["redact"] = new JsonArray([.. Redact.Select(r => (JsonNode)r)]), ["schema"] = JsonNode.Parse(Schema.GetRawText()),
                ["workflow"] = JsonNode.Parse(Workflow), ["templates"] = templates,
            };
            return CanonicalJson.Hash(JsonSerializer.SerializeToElement(node));
        }
    }

    /// <summary>What a type declaration may contain (design §5.1, phase-1-plan M2).</summary>
    public static partial class TypeRules
    {
        public const string SendOnlyWorkflow = """[{"step":"send"}]""";
        public const string DefaultLocale = "en";
        public const string Passthrough = "passthrough";
        public const int MaxTemplateBytes = 64 * 1024;

        [GeneratedRegex(@"^[a-z][a-z0-9-]{0,62}\z")]
        private static partial Regex Slug();

        [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,62}\z")]
        private static partial Regex KeySegment();

        [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*\z")]
        private static partial Regex Locale();

        [GeneratedRegex(@"^data(\.[A-Za-z0-9_-]{1,63}){1,10}\z")]
        private static partial Regex RedactPath();

        /// <summary>A type key: the application key, then one or more lowercase segments: <c>observability.alert.firing</c>.</summary>
        public static bool IsKeyOf(string applicationKey, string? key)
            => key is { Length: <= 200 } && key.StartsWith(applicationKey + ".", StringComparison.Ordinal)
            && key[(applicationKey.Length + 1)..].Split('.').All(s => KeySegment().IsMatch(s));

        internal static (CheckedType? Type, IReadOnlyList<FieldError> Errors) Check(string applicationKey, TypeDeclaration d, IChannelRegistry channels)
        {
            var errors = new List<FieldError>();
            void Fail(string pointer, string message) => errors.Add(new FieldError(pointer, message));

            if (!IsKeyOf(applicationKey, d.Key))
            {
                Fail("/key", $"The key is '{applicationKey}.' followed by lowercase segments separated by dots, up to 200 characters.");
            }
            var name = d.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 200)
            {
                Fail("/name", "The name is 1–200 characters.");
            }
            else if (name.Contains('\0'))
            {
                Fail("/name", Intake.RequestRules.NulMessage);
            }
            if (d.Category is null || !Slug().IsMatch(d.Category))
            {
                Fail("/category", "The category is 1–63 lowercase letters, digits or hyphens, starting with a letter.");
            }
            var displayGroup = d.DisplayGroup ?? DisplayGroups.General;
            if (!Slug().IsMatch(displayGroup))
            {
                Fail("/displayGroup", "The display group is 1–63 lowercase letters, digits or hyphens, starting with a letter.");
            }
            var priority = NotificationPriority.Normal;
            if (d.Priority is not null && !TryPriority(d.Priority, out priority))
            {
                Fail("/priority", "The priority is low, normal, high or critical.");
            }
            var correlation = d.Correlation ?? CorrelationRules.Optional;
            if (!CorrelationRules.All.Contains(correlation))
            {
                Fail("/correlation", "The correlation is none, optional or required.");
            }
            var redact = d.Redact ?? [];
            for (var i = 0; i < redact.Count; i++)
            {
                if (redact[i] is null || !RedactPath().IsMatch(redact[i]))
                {
                    Fail($"/redact/{i}", "A redacted path is data. followed by up to 10 property names: data.subject, data.user.email.");
                }
            }
            if (redact.Count > 50)
            {
                Fail("/redact", "Up to 50 redacted paths.");
            }
            if (d.Schema is not { } schema)
            {
                Fail("/schema", "The schema is required: a JSON Schema 2020-12 object for the request's data.");
            }
            else
            {
                errors.AddRange(DataSchemas.Check(schema, "/schema"));
            }
            if (d.Workflow is { } workflow && CanonicalJson.Write(workflow) != SendOnlyWorkflow)
            {
                Fail("/workflow", """Phase 1 runs one workflow: [{"step":"send"}]. Omit it or send exactly that.""");
            }
            var pointers = new Dictionary<(string, string), string>();
            var templates = CheckTemplates(d.Templates, channels, pointers, Fail);

            if (errors.Count > 0)
            {
                return (null, errors);
            }
            return (new CheckedType(d.Key!, name!, d.Category!, displayGroup, priority, d.Mandatory ?? false, d.ExternalRecipients ?? false, correlation,
                [.. redact], d.Schema!.Value, SendOnlyWorkflow, templates) { Pointers = pointers }, []);
        }

        public static bool TryPriority(string value, out NotificationPriority priority)
        {
            priority = NotificationPriority.Normal;
            return value is "low" or "normal" or "high" or "critical" && Enum.TryParse(value, ignoreCase: true, out priority);
        }

        private static Dictionary<string, IReadOnlyDictionary<string, string>> CheckTemplates(JsonElement? declared, IChannelRegistry channels,
            Dictionary<(string, string), string> pointers, Action<string, string> fail)
        {
            var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            if (declared is not { } templates || templates.ValueKind == JsonValueKind.Null)
            {
                return result;
            }
            if (templates.ValueKind != JsonValueKind.Object)
            {
                fail("/templates", "Templates map a channel to a template, or to an object of locale → template.");
                return result;
            }
            if (CanonicalJson.FirstDuplicate(templates, "/templates") is { } duplicate)
            {
                fail(duplicate, "This object repeats a name; each channel and locale appears once.");
                return result;
            }
            foreach (var entry in templates.EnumerateObject())
            {
                var pointer = "/templates/" + Pointer(entry.Name);
                if (channels.Find(entry.Name) is not { } channel)
                {
                    fail(pointer, $"There is no channel '{entry.Name}'. Channels: {string.Join(", ", channels.All.Select(c => c.Key).Order())}.");
                    continue;
                }
                var locales = new Dictionary<string, string>(StringComparer.Ordinal);
                if (entry.Value.ValueKind == JsonValueKind.String)
                {
                    locales[DefaultLocale] = entry.Value.GetString()!;
                    pointers[(entry.Name, DefaultLocale)] = pointer;
                }
                else if (entry.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var locale in entry.Value.EnumerateObject())
                    {
                        if (locale.Name.Length > 35 || !Locale().IsMatch(locale.Name))
                        {
                            fail(pointer + "/" + Pointer(locale.Name), "A locale is a language tag of up to 35 characters: en, vi, pt-BR.");
                        }
                        else if (locale.Value.ValueKind != JsonValueKind.String)
                        {
                            fail(pointer + "/" + Pointer(locale.Name), "A template is a string.");
                        }
                        // Language tags are case-insensitive (BCP 47): stored lowercase, so pt-BR and pt-br are one locale, declared once.
                        else if (!locales.TryAdd(locale.Name.ToLowerInvariant(), locale.Value.GetString()!))
                        {
                            fail(pointer + "/" + Pointer(locale.Name), "This locale is already declared for the channel (locales ignore case).");
                        }
                        else
                        {
                            // Errors point at the locale as sent (pt-BR), though it is stored lowercase.
                            pointers[(entry.Name, locale.Name.ToLowerInvariant())] = pointer + "/" + Pointer(locale.Name);
                        }
                    }
                    if (!locales.ContainsKey(DefaultLocale))
                    {
                        fail(pointer, "Each channel has an en template: it's the fallback for every other locale.");
                    }
                }
                else
                {
                    fail(pointer, "A template is a string, or an object of locale → template.");
                    continue;
                }
                foreach (var (locale, body) in locales)
                {
                    var at = pointers[(entry.Name, locale)];
                    if (body.Contains('\0'))
                    {
                        fail(at, Intake.RequestRules.NulMessage);
                        continue;
                    }
                    if (body == Passthrough)
                    {
                        if (!channel.Passthrough)
                        {
                            fail(at, $"The {channel.Key} channel needs a template; only channels that send data as is accept \"passthrough\".");
                        }
                        continue;
                    }
                    if (System.Text.Encoding.UTF8.GetByteCount(body) > MaxTemplateBytes)
                    {
                        fail(at, $"A template is at most {MaxTemplateBytes / 1024} KB.");
                        continue;
                    }
                    var parsed = Scriban.Template.Parse(body);
                    if (parsed.HasErrors)
                    {
                        fail(at, "The template doesn't parse: " + string.Join("; ", parsed.Messages.Select(m => m.ToString())));
                    }
                }
                result[entry.Name] = locales;
            }
            return result;
        }

        /// <summary>A JSON pointer segment (RFC 6901).</summary>
        internal static string Pointer(string name) => name.Replace("~", "~0").Replace("/", "~1");
    }

    internal sealed class TypeHandlers(NotificationsDbContext db, IChannelRegistry channels, TimeProvider clock)
        : IRequestHandler<DeclareType, Outcome<TypeVersionView>>
    {
        public async ValueTask<Outcome<TypeVersionView>> Handle(DeclareType request, CancellationToken cancellationToken)
        {
            var (type, errors) = TypeRules.Check(request.ApplicationKey, request.Declaration, channels);
            if (type is null)
            {
                return Outcome<TypeVersionView>.Invalid(ErrorCodes.InvalidType, "The type declaration has errors.", errors);
            }
            // Each template also runs once in the sandbox (design §8.2): what it uses outside it (include, import, the clock) is refused now,
            // not at its first delivery.
            var sandbox = new List<FieldError>();
            foreach (var (channel, locales) in type.Templates)
            {
                foreach (var (locale, body) in locales.Where(l => l.Value != TypeRules.Passthrough))
                {
                    if (await Rendering.TemplateRenderer.CheckAsync(body, cancellationToken) is { } problem)
                    {
                        sandbox.Add(new FieldError(type.Pointers[(channel, locale)], problem));
                    }
                }
            }
            if (sandbox.Count > 0)
            {
                return Outcome<TypeVersionView>.Invalid(ErrorCodes.InvalidType, "A template uses something the sandbox doesn't allow.", sandbox);
            }
            var hash = type.Hash();
            var latest = await TypeQueries.LatestAsync(db, request.ApplicationId, type.Key, cancellationToken);
            if (latest is not null && latest.DeclarationHash == hash)
            {
                return Outcome<TypeVersionView>.Ok(TypeQueries.View(latest, isNew: false));
            }
            // A stored hash can predate what hashes cover now (a version declared before 'correlation' existed): hash the stored version
            // as it would be declared today, so the same declaration still returns it rather than adding a version.
            if (latest is not null)
            {
                var stored = await db.Templates.AsNoTracking().Where(t => t.TypeId == latest.Id && t.TenantId == null).ToListAsync(cancellationToken);
                if (CheckedType.From(latest, stored).Hash() == hash)
                {
                    await db.Types.Where(t => t.Id == latest.Id).ExecuteUpdateAsync(set => set.SetProperty(t => t.DeclarationHash, hash), cancellationToken);
                    return Outcome<TypeVersionView>.Ok(TypeQueries.View(latest, isNew: false) with { DeclarationHash = hash });
                }
            }
            // Two different declarations at once both take latest + 1; the second commit hits the version's key, and the API answers it
            // (ApplicationEndpoints' race table): the same declaration as the winner gets the winner's version, a different one concurrent_change.
            var now = clock.UtcNow();
            var created = new NotificationType
            {
                ApplicationId = request.ApplicationId, Key = type.Key, Version = (latest?.Version ?? 0) + 1, Name = type.Name, Category = type.Category,
                DisplayGroup = type.DisplayGroup, Priority = type.Priority, Mandatory = type.Mandatory, ExternalRecipients = type.ExternalRecipients,
                Correlation = type.Correlation, Schema = CanonicalJson.Write(type.Schema), SchemaHash = CanonicalJson.Hash(type.Schema),
                Workflow = type.Workflow, Redact = [.. type.Redact], DeclarationHash = hash, CreatedAt = now,
            };
            db.Types.Add(created);
            foreach (var (channel, locales) in type.Templates)
            {
                foreach (var (locale, body) in locales)
                {
                    db.Templates.Add(new Template
                    {
                        TypeId = created.Id, Channel = channel, Locale = locale, Version = 1, CreatedAt = now,
                        Engine = body == TypeRules.Passthrough ? TemplateEngines.Passthrough : TemplateEngines.Scriban,
                        Body = body == TypeRules.Passthrough ? "" : body,
                    });
                }
            }
            return Outcome<TypeVersionView>.Created(TypeQueries.View(created, isNew: true));
        }
    }

    internal static class TypeQueries
    {
        public static Task<NotificationType?> LatestAsync(NotificationsDbContext db, Guid applicationId, string key, CancellationToken ct)
            => db.Types.AsNoTracking().Where(t => t.ApplicationId == applicationId && t.Key == key).OrderByDescending(t => t.Version).FirstOrDefaultAsync(ct);

        public static TypeVersionView View(NotificationType t, bool isNew) => new(t.Key, t.Version, t.Name, t.SchemaHash, t.DeclarationHash, t.CreatedAt, isNew);
    }

    /// <summary>Reads type versions, outside any command.</summary>
    public sealed class TypeReader(NotificationsDbContext db)
    {
        public async Task<IReadOnlyList<TypeVersionView>> VersionsAsync(Guid applicationId, string key, CancellationToken ct)
            => (await db.Types.AsNoTracking().Where(t => t.ApplicationId == applicationId && t.Key == key).OrderBy(t => t.Version).ToListAsync(ct))
                .Select(t => TypeQueries.View(t, isNew: false)).ToList();

        public Task<NotificationType?> LatestAsync(Guid applicationId, string key, CancellationToken ct) => TypeQueries.LatestAsync(db, applicationId, key, ct);

        /// <summary>The hash a declaration would get, to tell whether a racing declaration was the same one (null when it doesn't pass the rules).</summary>
        public static string? HashOf(string applicationKey, TypeDeclaration declaration, IChannelRegistry channels)
            => TypeRules.Check(applicationKey, declaration, channels).Type?.Hash();

        public async Task<TypeVersionDetail?> VersionAsync(Guid applicationId, string key, int version, CancellationToken ct)
        {
            var type = await db.Types.AsNoTracking().SingleOrDefaultAsync(t => t.ApplicationId == applicationId && t.Key == key && t.Version == version, ct);
            if (type is null)
            {
                return null;
            }
            var templates = await db.Templates.AsNoTracking().Where(t => t.TypeId == type.Id && t.TenantId == null).ToListAsync(ct);
            using var schema = JsonDocument.Parse(type.Schema);
            using var workflow = JsonDocument.Parse(type.Workflow);
            return new TypeVersionDetail(type.Key, type.Version, type.Name, type.Category, type.DisplayGroup, type.Priority, type.Mandatory,
                type.ExternalRecipients, type.Correlation, type.Redact, schema.RootElement.Clone(), workflow.RootElement.Clone(),
                templates.GroupBy(t => t.Channel).ToDictionary(g => g.Key,
                    g => (IReadOnlyDictionary<string, string>)g.ToDictionary(t => t.Locale, t => t.Engine == TemplateEngines.Passthrough ? TypeRules.Passthrough : t.Body)),
                type.SchemaHash, type.CreatedAt);
        }
    }
}
