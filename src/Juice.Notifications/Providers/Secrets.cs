using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers
{
    /// <summary>Configuration section <c>Secrets</c> (phase-4-plan p4-d5, I47): the deployment's vaults and how the service signs in to them.</summary>
    public sealed class SecretsOptions
    {
        public const string Section = "Secrets";

        /// <summary>How long a value read from a vault is kept: a rotated secret is in use this long after it changes.</summary>
        public TimeSpan CacheFor { get; set; } = TimeSpan.FromMinutes(5);

        public VaultOptions? Vault { get; set; }

        public KeyVaultOptions? KeyVault { get; set; }
    }

    /// <summary>HashiCorp Vault, KV version 2: a token (<see cref="TokenRef"/>) or AppRole (<see cref="RoleId"/> and <see cref="SecretIdRef"/>).</summary>
    public sealed class VaultOptions
    {
        public Uri? Address { get; set; }
        /// <summary>The KV v2 engine's mount.</summary>
        public string Mount { get; set; } = "secret";
        /// <summary>A Vault Enterprise namespace, sent as <c>X-Vault-Namespace</c>.</summary>
        public string? Namespace { get; set; }
        /// <summary>An <c>env:</c> or <c>file:</c> reference to a token.</summary>
        public string? TokenRef { get; set; }
        public string? RoleId { get; set; }
        /// <summary>An <c>env:</c> or <c>file:</c> reference to the AppRole's secret id.</summary>
        public string? SecretIdRef { get; set; }
    }

    /// <summary>
    /// Azure Key Vault: managed identity by default (a user-assigned one names its <see cref="ClientId"/>); client credentials when
    /// <see cref="TenantId"/>, <see cref="ClientId"/> and <see cref="ClientSecretRef"/> are set.
    /// </summary>
    public sealed class KeyVaultOptions
    {
        /// <summary><c>https://{name}.vault.azure.net/</c>.</summary>
        public Uri? VaultUri { get; set; }
        public string? TenantId { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecretRef { get; set; }
    }

    /// <summary>A vault couldn't be reached or answered with an error: worth trying again, unlike a secret nobody set.</summary>
    public sealed class SecretUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

    /// <summary>Reads a secret reference's value when it is needed (design D15): <c>env:</c>, <c>file:</c>, <c>vault:</c>, <c>keyvault:</c>.</summary>
    public interface ISecretResolver
    {
        /// <summary>
        /// The value. Throws <see cref="InvalidOperationException"/> when it isn't set (or its vault isn't configured), and
        /// <see cref="SecretUnavailableException"/> when its vault can't be read now.
        /// </summary>
        ValueTask<string> ResolveAsync(string reference, CancellationToken ct);
    }

    /// <summary>The client the vault readers use (as <see cref="ProviderHttp.Handler"/>); tests replace it.</summary>
    public sealed class SecretsHttp(HttpMessageHandler handler) : IDisposable
    {
        public HttpClient Client { get; } = new(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };

        public void Dispose() => Client.Dispose();
    }

    /// <summary>
    /// Secret references (phase-4-plan I47): <c>env:</c> and <c>file:</c> as before; <c>vault:{path}#{field}</c> from Vault's KV v2 engine and
    /// <c>keyvault:{name}</c> from Azure Key Vault, each value kept <see cref="SecretsOptions.CacheFor"/>, and the sign-in tokens until shortly before
    /// they expire. A vault that answers an error, or none, is <see cref="SecretUnavailableException"/>; a secret it says doesn't exist is unset.
    /// </summary>
    public sealed class SecretResolver(SecretsHttp http, IOptions<SecretsOptions> options, TimeProvider clock, ILogger<SecretResolver> logger) : ISecretResolver
    {
        private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset Until)> _values = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _tokens = new(StringComparer.Ordinal);

        public async ValueTask<string> ResolveAsync(string reference, CancellationToken ct)
        {
            if (!reference.StartsWith(SecretReferences.Vault, StringComparison.Ordinal) && !reference.StartsWith(SecretReferences.KeyVault, StringComparison.Ordinal))
            {
                return SecretReferences.Resolve(reference);
            }
            var now = clock.GetUtcNow();
            if (_values.TryGetValue(reference, out var cached) && cached.Until > now)
            {
                return cached.Value;
            }
            var value = reference.StartsWith(SecretReferences.Vault, StringComparison.Ordinal)
                ? await VaultAsync(reference[SecretReferences.Vault.Length..], ct)
                : await KeyVaultAsync(reference[SecretReferences.KeyVault.Length..], ct);
            _values[reference] = (value, now + options.Value.CacheFor);
            return value;
        }

        private async Task<string> VaultAsync(string pathAndField, CancellationToken ct)
        {
            var vault = options.Value.Vault;
            if (vault?.Address is not { } address)
            {
                throw new InvalidOperationException("A vault: secret is named, but Secrets:Vault:Address isn't configured.");
            }
            var hash = pathAndField.LastIndexOf('#');
            if (hash <= 0 || hash == pathAndField.Length - 1)
            {
                throw new InvalidOperationException($"The reference vault:{pathAndField} names no field: vault:path#field.");
            }
            var (path, field) = (pathAndField[..hash].Trim('/'), pathAndField[(hash + 1)..]);
            var token = await VaultTokenAsync(vault, address, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, $"v1/{vault.Mount.Trim('/')}/data/{path}"));
            request.Headers.TryAddWithoutValidation("X-Vault-Token", token);
            if (vault.Namespace is { Length: > 0 } space)
            {
                request.Headers.TryAddWithoutValidation("X-Vault-Namespace", space);
            }
            var answer = await SendAsync(request, "Vault", ct);
            if (answer.Status == 404)
            {
                throw new InvalidOperationException($"Vault has no secret at {path}.");
            }
            if (answer.Status is 401 or 403)
            {
                _tokens.TryRemove("vault", out _);
                throw new SecretUnavailableException($"Vault refused to read {path} ({answer.Status}).");
            }
            Ensure(answer, "Vault");
            using var json = JsonDocument.Parse(answer.Body);
            return json.RootElement.TryGetProperty("data", out var outer) && outer.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidOperationException($"Vault's secret at {path} has no field {field}.");
        }

        /// <summary>The token: configured, or from an AppRole login, kept until a minute before its lease ends.</summary>
        private async Task<string> VaultTokenAsync(VaultOptions vault, Uri address, CancellationToken ct)
        {
            if (vault.TokenRef is { } tokenRef)
            {
                return SecretReferences.Resolve(tokenRef);
            }
            if (vault.RoleId is null || vault.SecretIdRef is null)
            {
                throw new InvalidOperationException("Secrets:Vault signs in with TokenRef, or RoleId and SecretIdRef (AppRole); neither is configured.");
            }
            var now = clock.GetUtcNow();
            if (_tokens.TryGetValue("vault", out var cached) && cached.Until > now)
            {
                return cached.Token;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, "v1/auth/approle/login"))
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { role_id = vault.RoleId, secret_id = SecretReferences.Resolve(vault.SecretIdRef) })),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (vault.Namespace is { Length: > 0 } space)
            {
                request.Headers.TryAddWithoutValidation("X-Vault-Namespace", space);
            }
            var answer = await SendAsync(request, "Vault's AppRole login", ct);
            Ensure(answer, "Vault's AppRole login");
            using var json = JsonDocument.Parse(answer.Body);
            var auth = json.RootElement.GetProperty("auth");
            var token = auth.GetProperty("client_token").GetString()!;
            var lease = auth.TryGetProperty("lease_duration", out var d) && d.TryGetInt32(out var seconds) ? seconds : 300;
            _tokens["vault"] = (token, now.AddSeconds(Math.Max(30, lease - 60)));
            return token;
        }

        private async Task<string> KeyVaultAsync(string name, CancellationToken ct)
        {
            var kv = options.Value.KeyVault;
            if (kv?.VaultUri is not { } vaultUri)
            {
                throw new InvalidOperationException("A keyvault: secret is named, but Secrets:KeyVault:VaultUri isn't configured.");
            }
            var token = await AzureTokenAsync(kv, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(vaultUri, $"secrets/{Uri.EscapeDataString(name)}?api-version=7.4"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var answer = await SendAsync(request, "Key Vault", ct);
            if (answer.Status == 404)
            {
                throw new InvalidOperationException($"Key Vault has no secret {name}.");
            }
            if (answer.Status is 401 or 403)
            {
                _tokens.TryRemove("azure", out _);
                throw new SecretUnavailableException($"Key Vault refused to read {name} ({answer.Status}).");
            }
            Ensure(answer, "Key Vault");
            return answer.Field("value") is { Length: > 0 } value ? value : throw new InvalidOperationException($"Key Vault's secret {name} is empty.");
        }

        /// <summary>An access token for Key Vault: client credentials when configured, else the managed identity (the instance metadata endpoint).</summary>
        private async Task<string> AzureTokenAsync(KeyVaultOptions kv, CancellationToken ct)
        {
            var now = clock.GetUtcNow();
            if (_tokens.TryGetValue("azure", out var cached) && cached.Until > now)
            {
                return cached.Token;
            }
            HttpRequestMessage request;
            if (kv is { TenantId: { } tenant, ClientId: { } clientId, ClientSecretRef: { } secretRef })
            {
                request = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token")
                {
                    Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("client_id", clientId),
                        new("client_secret", SecretReferences.Resolve(secretRef)), new("scope", "https://vault.azure.net/.default")]),
                };
            }
            else
            {
                var query = "api-version=2018-02-01&resource=" + Uri.EscapeDataString("https://vault.azure.net") + (kv.ClientId is { } id ? "&client_id=" + Uri.EscapeDataString(id) : "");
                request = new HttpRequestMessage(HttpMethod.Get, "http://169.254.169.254/metadata/identity/oauth2/token?" + query);
                request.Headers.TryAddWithoutValidation("Metadata", "true");
            }
            using (request)
            {
                var answer = await SendAsync(request, "Azure sign-in", ct);
                Ensure(answer, "Azure sign-in");
                var token = answer.Field("access_token") ?? throw new SecretUnavailableException("Azure sign-in answered without a token.");
                int seconds;
                using (var json = JsonDocument.Parse(answer.Body))
                {
                    var e = json.RootElement.GetProperty("expires_in");
                    seconds = e.ValueKind == JsonValueKind.Number ? e.GetInt32() : int.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                }
                _tokens["azure"] = (token, now.AddSeconds(Math.Max(30, seconds - 300)));
                return token;
            }
        }

        private async Task<ApiAnswer> SendAsync(HttpRequestMessage request, string what, CancellationToken ct)
        {
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, TimeSpan.FromSeconds(10), ct);
            if (answer is null)
            {
                logger.LogWarning("{What} didn't answer: {Error}", what, failure!.Error);
                throw new SecretUnavailableException($"{what} didn't answer: {failure.Error}");
            }
            return answer;
        }

        private static void Ensure(ApiAnswer answer, string what)
        {
            if (answer.Status is not (>= 200 and < 300))
            {
                throw new SecretUnavailableException($"{what} answered {answer.Status}.");
            }
        }
    }

    public static class SecretRegistration
    {
        /// <summary>The secret resolver and its options (section <c>Secrets</c>).</summary>
        public static IServiceCollection AddSecretResolver(this IServiceCollection services)
        {
            services.AddOptions<SecretsOptions>();
            services.TryAddSingleton(sp => new SecretsHttp(ProviderHttp.Handler(sp.GetRequiredService<IOptions<DeliveryOptions>>().Value)));
            services.TryAddSingleton<ISecretResolver, SecretResolver>();
            return services;
        }
    }
}
