using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Juice.Notifications.Intake;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Planning
{
    // The identity resolver (design §6.1, phase-2-plan p2-d1, M16): users, groups and roles from the identity provider, read through an
    // IIdentityDirectory. The directories are the deployment's (Identity:Directories), like SMTP servers: a tenant's resolver settings name one,
    // so the API can't point the service's credentials at a host of an admin's choosing. A person belongs to a tenant through a user attribute
    // (platform_tenant, the one that issues the token's claim); a directory never returns a person of another tenant, or a disabled one.

    /// <summary>Configuration section <c>Identity</c>: the identity directories the deployment reads people from, by name.</summary>
    public sealed class IdentityOptions
    {
        public const string Section = "Identity";

        public Dictionary<string, KeycloakDirectoryOptions> Directories { get; set; } = new(StringComparer.Ordinal);

        /// <summary>The first directory's problem, named (<c>Identity:Directories:keycloak: …</c>), or null when all are fine.</summary>
        internal string? Problem()
        {
            foreach (var (name, directory) in Directories)
            {
                var problem = !Configuration.ConfigurationRules.Key().IsMatch(name) ? "the name is 1–63 lowercase letters, digits or hyphens, starting with a letter"
                    : directory is null ? "BaseUrl, Realm, ClientId and ClientSecretRef are required"
                    : directory.Problem();
                if (problem is not null)
                {
                    return $"{Section}:Directories:{name}: {problem}.";
                }
            }
            return null;
        }
    }

    internal sealed class IdentityOptionsValidation : IValidateOptions<IdentityOptions>
    {
        public ValidateOptionsResult Validate(string? name, IdentityOptions options)
            => options.Problem() is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
    }

    /// <summary>A Keycloak realm, read through its admin REST API as a service account.</summary>
    /// <remarks>
    /// The account needs the realm-management roles <c>view-users</c> (users, groups and their members) and <c>view-realm</c> (a realm role's
    /// holders). Never <c>view-clients</c>: it reads every client's secret, so client roles aren't resolved (deferred #43).
    /// </remarks>
    public sealed class KeycloakDirectoryOptions
    {
        /// <summary>The server, without the realm: <c>https://id.example.com</c>.</summary>
        public string BaseUrl { get; set; } = "";
        public string Realm { get; set; } = "";
        public string ClientId { get; set; } = "";
        /// <summary>A secret reference (<c>env:</c>, <c>file:</c>, design D15) to the client's secret.</summary>
        public string ClientSecretRef { get; set; } = "";
        /// <summary>
        /// The subscribers' source. <c>oidc</c> when the directory is the provider people sign in with, so the person a resolver reaches and the
        /// signed-in person (the token's <c>sub</c>) are one subscriber (phase-2-plan I16).
        /// </summary>
        public string Source { get; set; } = "oidc";
        /// <summary>The user attribute that says which tenant a person belongs to (its value is the tenant id).</summary>
        public string TenantAttribute { get; set; } = "platform_tenant";
        public int PageSize { get; set; } = 100;
        /// <summary>
        /// The most members of a group or holders of a role read for one name. Groups and roles span tenants, so a name may read many people of
        /// other tenants to find its own; past this many it stops and the name is recorded as truncated. Reading them is part of one planner
        /// step, so MaxScanned / PageSize requests must fit well within the planner's lease (<c>Stages:Planner:Lease</c>, 30 s): a step that
        /// outlasts it is taken over by another worker and its work thrown away.
        /// </summary>
        public int MaxScanned { get; set; } = 10_000;
        /// <summary>One request's limit.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>
        /// One name's limit, all its requests together (the token, the group's lookup, every page). Past it, or past the planner step's own
        /// deadline (<see cref="ResolveContext.StepDeadline"/>, two thirds of its lease) if that comes first, a listing keeps the people read so
        /// far and is truncated, and a name with nothing read yet is unavailable (I22).
        /// </summary>
        public TimeSpan NameBudget { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>The most either limit may be (a sanity bound: CancelAfter throws past ~24 days). The step's deadline is the working limit.</summary>
        internal static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);

        internal string? Problem()
        {
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.Query)
                || !string.IsNullOrEmpty(url.Fragment))
            {
                return "BaseUrl is an absolute http(s) URL with no query or fragment";
            }
            if (string.IsNullOrWhiteSpace(Realm) || string.IsNullOrWhiteSpace(ClientId))
            {
                return "Realm and ClientId are required";
            }
            if (!Providers.SecretReferences.IsLocal(ClientSecretRef))
            {
                return "ClientSecretRef is a secret reference: env:NAME or file:/path";
            }
            if (!Configuration.ConfigurationRules.Key().IsMatch(Source))
            {
                return "Source is 1–63 lowercase letters, digits or hyphens, starting with a letter";
            }
            if (string.IsNullOrWhiteSpace(TenantAttribute))
            {
                return "TenantAttribute is required";
            }
            // Timeout bounded: CancelAfter throws past ~24 days, which would fail every step rather than make the directory unavailable.
            return PageSize is < 1 or > 1000 || MaxScanned < PageSize || Timeout <= TimeSpan.Zero || Timeout > MaxTimeout
                || NameBudget <= TimeSpan.Zero || NameBudget > MaxTimeout
                ? "PageSize is 1–1000, MaxScanned at least PageSize, and Timeout and NameBudget positive and at most 5 minutes"
                : null;
        }
    }

    /// <summary>
    /// People of one tenant from an identity directory. <c>Truncated</c>: the directory stopped reading before the end (more than the bound,
    /// or past its scan limit). Throws <see cref="ResolverUnavailableException"/> when the directory can't be read.
    /// </summary>
    public interface IIdentityDirectory
    {
        /// <summary>Whether the deployment configured a directory of this name.</summary>
        bool Has(string directory);

        IReadOnlyCollection<string> Names { get; }

        /// <summary>The user of this id, when it is an enabled member of the tenant; otherwise none.</summary>
        /// <remarks>
        /// <paramref name="deadline"/>, here and below: when the caller's time is up (the planner step's), which ends the work as the name's own
        /// budget does; <paramref name="ct"/> cancels it outright.
        /// </remarks>
        Task<IReadOnlyList<ResolvedPerson>> UserAsync(string directory, Guid tenantId, string id, CancellationToken deadline, CancellationToken ct);

        /// <summary>
        /// A group's direct members in the tenant (a group by its path: <c>ops</c>, <c>eng/sre</c>), all of them: the planner keeps the first
        /// by id when there are more than it may reach, which only holds if it sees them all (to the directory's scan limit).
        /// </summary>
        Task<(IReadOnlyList<ResolvedPerson> People, bool Truncated)> GroupMembersAsync(string directory, Guid tenantId, string path, CancellationToken deadline,
            CancellationToken ct);

        /// <summary>A realm role's direct holders in the tenant, all of them, as for a group.</summary>
        Task<(IReadOnlyList<ResolvedPerson> People, bool Truncated)> RoleHoldersAsync(string directory, Guid tenantId, string role, CancellationToken deadline,
            CancellationToken ct);
    }

    /// <summary>
    /// <c>identity</c> (design §6.1): <c>user</c>, <c>group</c> and <c>role</c> from the directory the tenant's settings name
    /// (<c>{"directory": "keycloak"}</c>). An email comes as a contact point, verified only when the directory says so. Expansions are kept in
    /// the <see cref="ResolverCache"/>, a user's lookup too: a person who left a group or the tenant may be reached until the entry expires.
    /// </summary>
    public sealed class IdentityRecipientResolver(IIdentityDirectory directory, ResolverCache cache) : IRecipientResolver
    {
        public const string Kind = "identity";

        public string Key => Kind;

        public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { RecipientKinds.User, RecipientKinds.Group, RecipientKinds.Role };

        public string? CheckSettings(string settingsJson)
        {
            var known = directory.Names.Count > 0 ? $"The deployment's directories are: {string.Join(", ", directory.Names.Order(StringComparer.Ordinal))}." : "The deployment configures no identity directory.";
            using var settings = JsonDocument.Parse(settingsJson);
            if (settings.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "Settings are a JSON object.";
            }
            string? name = null;
            foreach (var property in settings.RootElement.EnumerateObject())
            {
                if (property.Name != "directory")
                {
                    return $"An identity resolver's settings name its directory only (\"directory\"), not {property.Name}.";
                }
                name = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
            return name is not null && directory.Has(name) ? null : $"Name the directory to read people from: {{\"directory\": \"…\"}}. {known}";
        }

        public async ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct)
        {
            // Checked when saved. A directory the deployment has since removed is unavailable: the name waits, then is recorded as unreached (I22).
            if (context.Config is not { } config || Directory(config.Settings) is not { } name || !directory.Has(name))
            {
                throw new ResolverUnavailableException($"The identity resolver {context.Config?.Key} names no directory the deployment configures.");
            }
            var tenant = context.Request.TenantId;
            // A group's path as Keycloak writes it (/ops) or without the slash (ops): one group.
            var id = spec.Kind == RecipientKinds.Group && spec.Id is ['/', .. var rest] ? rest : spec.Id;
            // What a URL path can't carry as one segment (or as a path of them) reaches no one, without asking.
            if (id is null || !Reachable(id, spec.Kind == RecipientKinds.Group))
            {
                return new ResolveResult([]);
            }
            return await cache.GetOrLoadAsync(config, spec.Kind + ":" + id, async token =>
            {
                switch (spec.Kind)
                {
                    case RecipientKinds.User:
                        return new ResolveResult([]) { People = await directory.UserAsync(name, tenant, id, context.StepDeadline, token) };
                    case RecipientKinds.Group:
                        var (members, groupTruncated) = await directory.GroupMembersAsync(name, tenant, id, context.StepDeadline, token);
                        return new ResolveResult([], groupTruncated) { People = members, Partial = members is KeycloakIdentityDirectory.PartialRead };
                    default:
                        var (holders, roleTruncated) = await directory.RoleHoldersAsync(name, tenant, id, context.StepDeadline, token);
                        return new ResolveResult([], roleTruncated) { People = holders, Partial = holders is KeycloakIdentityDirectory.PartialRead };
                }
            }, ct);
        }

        private static string? Directory(string settingsJson)
        {
            using var settings = JsonDocument.Parse(settingsJson);
            return settings.RootElement.ValueKind == JsonValueKind.Object && settings.RootElement.TryGetProperty("directory", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        /// <summary>No empty, <c>.</c> or <c>..</c> segment, which a URL would resolve to another resource; a group's path may have several.</summary>
        internal static bool Reachable(string id, bool path)
            => id.Length > 0 && (path ? id.Split('/') : [id]).All(s => s is not ("" or "." or ".."));
    }

    /// <summary>
    /// <see cref="IIdentityDirectory"/> over Keycloak's admin REST API (phase-2-plan p2-d1). A client-credentials token per directory is reused
    /// until shortly before it expires. A timeout, a refused connection, a 5xx or 429, and a refusal (401 or 403, or a 400 from the token
    /// endpoint: the account or its roles) make the directory unavailable (I22); a 404, or a 400 for a name Keycloak won't take, is no one.
    /// </summary>
    public sealed class KeycloakIdentityDirectory : IIdentityDirectory, IDisposable
    {
        private readonly IOptions<IdentityOptions> _options;
        private readonly TimeProvider _clock;
        private readonly ILogger<KeycloakIdentityDirectory> _logger;
        private readonly HttpClient _http;
        private readonly Dictionary<string, (SemaphoreSlim Gate, TokenHolder Token)> _tokens;
        /// <summary>
        /// When each refusal (directory, resource kind, status) was last logged at Error: once a minute, then Debug, so a wrong secret doesn't
        /// flood the log, and a second, different problem still shows.
        /// </summary>
        private readonly Dictionary<(string Directory, string Resource, int Status), DateTimeOffset> _refusalLogged = [];

        private sealed class TokenHolder
        {
            public string? Value;
            public DateTimeOffset Until;
        }

        public KeycloakIdentityDirectory(IOptions<IdentityOptions> options, TimeProvider clock, ILogger<KeycloakIdentityDirectory> logger)
            : this(options, clock, logger, new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false })
        {
        }

        /// <summary>Over the given handler (the unit tests' fake server).</summary>
        internal KeycloakIdentityDirectory(IOptions<IdentityOptions> options, TimeProvider clock, ILogger<KeycloakIdentityDirectory> logger,
            HttpMessageHandler handler)
        {
            (_options, _clock, _logger) = (options, clock, logger);
            _http = new HttpClient(handler)
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan, // per request, from the directory's options
                MaxResponseContentBufferSize = 16 * 1024 * 1024,
            };
            _tokens = options.Value.Directories.Keys.ToDictionary(k => k, _ => (new SemaphoreSlim(1, 1), new TokenHolder()), StringComparer.Ordinal);
        }

        public bool Has(string directory) => _options.Value.Directories.ContainsKey(directory);

        public IReadOnlyCollection<string> Names => _options.Value.Directories.Keys;

        public Task<IReadOnlyList<ResolvedPerson>> UserAsync(string directory, Guid tenantId, string id, CancellationToken deadline, CancellationToken ct)
            => WithinBudgetAsync<IReadOnlyList<ResolvedPerson>>(directory, "user " + id, async (settings, token) =>
            {
                using var user = await GetAsync(directory, settings, $"users/{Uri.EscapeDataString(id)}", token);
                return user is not null && Person(settings, tenantId, user.RootElement) is { } person ? [person] : [];
            }, null, deadline, ct);

        public Task<(IReadOnlyList<ResolvedPerson> People, bool Truncated)> GroupMembersAsync(string directory, Guid tenantId, string path,
            CancellationToken deadline, CancellationToken ct)
        {
            var read = new Progress();
            return WithinBudgetAsync(directory, "group " + path, (settings, token) => GroupMembersAsync(directory, settings, tenantId, path, read, token),
                read.Partial, deadline, ct);
        }

        public Task<(IReadOnlyList<ResolvedPerson> People, bool Truncated)> RoleHoldersAsync(string directory, Guid tenantId, string role,
            CancellationToken deadline, CancellationToken ct)
        {
            var read = new Progress();
            return WithinBudgetAsync(directory, "role " + role,
                (settings, token) => PagesAsync(directory, settings, tenantId, $"roles/{Uri.EscapeDataString(role)}/users", read, token), read.Partial, deadline, ct);
        }

        /// <summary>The people a listing had read when its time ran out: not to be cached (the next read may finish).</summary>
        internal sealed class PartialRead(IEnumerable<ResolvedPerson> people) : List<ResolvedPerson>(people);

        /// <summary>A listing's people so far, and how many pages they came from.</summary>
        private sealed class Progress
        {
            public readonly List<ResolvedPerson> People = [];
            public int Pages;

            /// <summary>What was read when time ran out: truncated, if any page was; nothing to offer otherwise.</summary>
            public (bool Has, (IReadOnlyList<ResolvedPerson> People, bool Truncated) Value) Partial()
                => Pages > 0 ? (true, (new PartialRead(People), true)) : (false, default);
        }

        /// <summary>
        /// One name's work within <see cref="KeycloakDirectoryOptions.NameBudget"/> and the caller's <paramref name="deadline"/>, whichever ends
        /// first. Then <paramref name="partial"/> offers what was read (a listing, truncated), or the directory is unavailable for the name, as
        /// for a request that timed out (I22). A directory the deployment doesn't configure is unavailable too.
        /// </summary>
        private async Task<T> WithinBudgetAsync<T>(string directory, string name, Func<KeycloakDirectoryOptions, CancellationToken, Task<T>> work,
            Func<(bool Has, T Value)>? partial, CancellationToken deadline, CancellationToken ct)
        {
            if (!_options.Value.Directories.TryGetValue(directory, out var settings) || !_tokens.ContainsKey(directory))
            {
                throw new ResolverUnavailableException($"There is no directory {directory} in this deployment's configuration.");
            }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline);
            budget.CancelAfter(settings.NameBudget);
            try
            {
                return await work(settings, budget.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                var why = deadline.IsCancellationRequested ? "the planner step's time ran out" : $"its {settings.NameBudget.TotalSeconds:0.#} s budget ran out";
                if (partial?.Invoke() is (true, var value))
                {
                    _logger.LogWarning("Directory {Directory} stopped reading {Name} when {Why}; it keeps the people read so far, truncated", directory, name, why);
                    return value;
                }
                throw new ResolverUnavailableException($"Directory {directory} didn't finish reading {name}: {why}.", ex);
            }
        }

        private async Task<(IReadOnlyList<ResolvedPerson> People, bool Truncated)> GroupMembersAsync(string directory, KeycloakDirectoryOptions settings,
            Guid tenantId, string path, Progress read, CancellationToken ct)
        {
            using var group = await GetAsync(directory, settings, "group-by-path/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)), ct);
            if (group is null)
            {
                return ([], false);
            }
            // Read only what the shape allows: TryGetProperty and GetString throw on another kind, which would fail the step instead (I22).
            if (group.RootElement.ValueKind != JsonValueKind.Object || !group.RootElement.TryGetProperty("id", out var groupId)
                || groupId.ValueKind != JsonValueKind.String || groupId.GetString() is not { Length: > 0 } gid)
            {
                throw new ResolverUnavailableException($"Directory {directory} answered group {path} with something other than a group.");
            }
            return await PagesAsync(directory, settings, tenantId, $"groups/{Uri.EscapeDataString(gid)}/members", read, ct);
        }

        /// <summary>
        /// The tenant's people on a listing, page by page, to its end or the scan limit, kept in <paramref name="read"/> as they come. Not stopped
        /// at the planner's bound: it keeps the first by id, and Keycloak lists by username. Past the scan limit, the name is truncated when the
        /// listing has anyone left. Only a missing first page means no one (the group or role isn't there); anything but a list after it means
        /// the listing changed or broke while being read, so the directory is unavailable rather than some of its people silently dropped.
        /// </summary>
        private async Task<(IReadOnlyList<ResolvedPerson> People, bool Truncated)> PagesAsync(string directory, KeycloakDirectoryOptions settings,
            Guid tenantId, string listing, Progress read, CancellationToken ct)
        {
            var people = read.People;
            for (var first = 0; ; )
            {
                var probe = first >= settings.MaxScanned;
                // The last page stops at the scan limit, so no more than MaxScanned are read; past it, one more says whether anyone is left.
                var size = probe ? 1 : Math.Min(settings.PageSize, settings.MaxScanned - first);
                using var page = await GetAsync(directory, settings, $"{listing}?first={first}&max={size}&briefRepresentation=false", ct,
                    nameRefusedMeansNoOne: first == 0);
                if (page is null && first == 0)
                {
                    return ([], false); // the group or role isn't there
                }
                if (page is not { RootElement.ValueKind: JsonValueKind.Array })
                {
                    throw new ResolverUnavailableException($"Directory {directory} answered {listing} (from {first}) with something other than a list.");
                }
                if (probe)
                {
                    var more = page.RootElement.GetArrayLength() > 0;
                    if (more)
                    {
                        _logger.LogWarning("Directory {Directory} stopped reading {Listing} for tenant {Tenant} after {Scanned} people (MaxScanned)",
                            directory, listing, tenantId, first);
                    }
                    return (people.ToList(), more);
                }
                var count = 0;
                foreach (var user in page.RootElement.EnumerateArray())
                {
                    count++;
                    if (Person(settings, tenantId, user) is { } person)
                    {
                        people.Add(person);
                    }
                }
                read.Pages++;
                if (count < size)
                {
                    return (people.ToList(), false);
                }
                first += size;
            }
        }

        /// <summary>The person, when enabled and a member of the tenant; else null.</summary>
        internal static ResolvedPerson? Person(KeycloakDirectoryOptions settings, Guid tenantId, JsonElement user)
        {
            if (user.ValueKind != JsonValueKind.Object || !user.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                || idElement.GetString() is not { Length: > 0 } id
                || !(user.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True))
            {
                return null;
            }
            var tenants = Attribute(user, settings.TenantAttribute);
            if (tenants.Count != 1 || !Guid.TryParse(tenants[0], out var tenant) || tenant != tenantId)
            {
                return null;
            }
            var name = string.Join(' ', new[] { Text(user, "firstName"), Text(user, "lastName") }.Where(n => !string.IsNullOrWhiteSpace(n))).Trim();
            var contacts = Text(user, "email") is { Length: > 0 } email
                ? [new ResolvedContact(Channels.Email, email, user.TryGetProperty("emailVerified", out var verified) && verified.ValueKind == JsonValueKind.True)]
                : new List<ResolvedContact>();
            return new ResolvedPerson(settings.Source, id, name.Length > 0 ? name : Text(user, "username"), Attribute(user, "locale").FirstOrDefault(),
                Attribute(user, "zoneinfo").FirstOrDefault(), contacts);
        }

        private static string? Text(JsonElement user, string property)
            => user.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static List<string> Attribute(JsonElement user, string name)
            => user.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object
                && attributes.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                : [];

        /// <summary>
        /// The resource under the realm's admin API, or null on 404 or 400 (a name Keycloak won't take). A stale token is renewed once.
        /// <paramref name="nameRefusedMeansNoOne"/>: whether the caller takes a null as no one, so a 400 is logged as such (a later page's
        /// null makes the directory unavailable instead, and says so itself).
        /// </summary>
        private async Task<JsonDocument?> GetAsync(string directory, KeycloakDirectoryOptions settings, string resource, CancellationToken ct,
            bool nameRefusedMeansNoOne = true)
        {
            var url = $"{settings.BaseUrl.TrimEnd('/')}/admin/realms/{Uri.EscapeDataString(settings.Realm)}/{resource}";
            string? rejected = null;
            while (true)
            {
                var token = await TokenAsync(directory, settings, rejected, ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await SendAsync(directory, settings, request, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized && rejected is null)
                {
                    rejected = token;
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    // A name Keycloak won't take: no one, as a missing one. (From the token endpoint a 400 is the account's: see TokenAsync.)
                    if (nameRefusedMeansNoOne)
                    {
                        _logger.LogWarning("Directory {Directory} refused the name in {Resource} (400); it reaches no one", directory, resource);
                    }
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw Unavailable(directory, settings, response.StatusCode, resource);
                }
                return await ReadAsync(directory, settings, response, ct);
            }
        }

        /// <summary>
        /// The directory's token. <paramref name="rejected"/>: the token the API just refused, renewed unless another caller already has, so
        /// concurrent refusals of one stale token fetch one new token, not one each.
        /// </summary>
        private async Task<string> TokenAsync(string directory, KeycloakDirectoryOptions settings, string? rejected, CancellationToken ct)
        {
            var (gate, holder) = _tokens[directory];
            await gate.WaitAsync(ct);
            try
            {
                var now = _clock.GetUtcNow();
                if (holder.Value is { } current && holder.Until > now && current != rejected)
                {
                    return current;
                }
                string secret;
                try
                {
                    secret = Providers.SecretReferences.Resolve(settings.ClientSecretRef);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    throw new ResolverUnavailableException($"Directory {directory}: {ex.Message}", ex);
                }
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{settings.BaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(settings.Realm)}/protocol/openid-connect/token")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials", ["client_id"] = settings.ClientId, ["client_secret"] = secret,
                    }),
                };
                using var response = await SendAsync(directory, settings, request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw Unavailable(directory, settings, response.StatusCode, "token");
                }
                using var body = await ReadAsync(directory, settings, response, ct);
                if (body.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("access_token", out var accessToken)
                    || accessToken.ValueKind != JsonValueKind.String || accessToken.GetString() is not { Length: > 0 } value)
                {
                    throw new ResolverUnavailableException($"Directory {directory} issued no access token.");
                }
                var lifetime = body.RootElement.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number
                    && expires.TryGetInt32(out var seconds) ? seconds : 60;
                // Renewed a little early (30 s, or half a shorter lifetime), so a token doesn't expire between being taken and being used, and a
                // short-lived one is still reused.
                (holder.Value, holder.Until) = (value, now.AddSeconds(Math.Max(0, lifetime - Math.Min(30, lifetime / 2))));
                return value;
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<HttpResponseMessage> SendAsync(string directory, KeycloakDirectoryOptions settings, HttpRequestMessage request, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(settings.Timeout);
            try
            {
                return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new ResolverUnavailableException($"Directory {directory} didn't answer within {settings.Timeout.TotalSeconds:0.#} s.", ex);
            }
            catch (HttpRequestException ex)
            {
                // The message names the host and port, never the credentials (they're in the body).
                throw new ResolverUnavailableException($"Directory {directory} can't be reached: {ex.Message}", ex);
            }
        }

        private static async Task<JsonDocument> ReadAsync(string directory, KeycloakDirectoryOptions settings, HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
            catch (JsonException ex)
            {
                throw new ResolverUnavailableException($"Directory {directory} answered with something other than JSON.", ex);
            }
        }

        private ResolverUnavailableException Unavailable(string directory, KeycloakDirectoryOptions settings, HttpStatusCode status, string resource)
        {
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                // Not an outage: the account, its secret or its roles. Said at Error, since waiting won't fix it; once a minute per directory,
                // resource kind (token, users, groups, roles) and status, as every name of every notification waiting on it says it again.
                var now = _clock.GetUtcNow();
                var key = (directory, resource.Split('/', '?')[0], (int)status);
                var level = LogLevel.Debug;
                lock (_refusalLogged)
                {
                    if (!_refusalLogged.TryGetValue(key, out var last) || now - last >= TimeSpan.FromMinutes(1))
                    {
                        _refusalLogged[key] = now;
                        level = LogLevel.Error;
                    }
                }
                _logger.Log(level, "Directory {Directory} refused {Resource} ({Status}): check client {Client}, its secret and its realm-management roles (view-users, view-realm)",
                    directory, resource, (int)status, settings.ClientId);
            }
            return new ResolverUnavailableException($"Directory {directory} answered {resource} with {(int)status}.");
        }

        public void Dispose()
        {
            _http.Dispose();
            foreach (var (gate, _) in _tokens.Values)
            {
                gate.Dispose();
            }
        }
    }
}
