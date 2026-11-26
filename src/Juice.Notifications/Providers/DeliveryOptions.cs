namespace Juice.Notifications.Providers
{
    /// <summary>Configuration section <c>Delivery</c>: the deployment's side of sending. Nothing here is settable through the API.</summary>
    public sealed class DeliveryOptions
    {
        public const string Section = "Delivery";

        /// <summary>Attempts before a retryable failure becomes final (design §10.3).</summary>
        public int MaxAttempts { get; set; } = 8;
        public TimeSpan RetryBase { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan RetryMax { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>How long a delivery of a tenant that isn't active is held before it is looked at again.</summary>
        public TimeSpan TenantHold { get; set; } = TimeSpan.FromMinutes(5);

        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>Response bytes read from a webhook; the rest is discarded.</summary>
        public int MaxResponseBytes { get; set; } = 64 * 1024;
        /// <summary>Plain HTTP to allowlisted private destinations (design §13.1). HTTPS is required everywhere else.</summary>
        public bool AllowHttpToAllowlisted { get; set; }

        /// <summary>A secret reference set through the API may name only environment variables with this prefix.</summary>
        public string ApiSecretEnvPrefix { get; set; } = "NOTIFICATIONS_SECRET_";
        /// <summary>A secret reference set through the API may name only files directly in this folder; unset refuses files.</summary>
        public string? ApiSecretDirectory { get; set; }
        /// <summary>A <c>vault:</c> reference set through the API names a path under this prefix (<c>notifications/</c>); unset refuses them.</summary>
        public string? ApiVaultPrefix { get; set; }
        /// <summary>A <c>keyvault:</c> reference set through the API names a secret starting with this (<c>notifications-</c>); unset refuses them.</summary>
        public string? ApiKeyVaultPrefix { get; set; }

        /// <summary>
        /// A secret reference a producer's compatibility alias writes (rows <see cref="ManagedBy.WebhooksAlias"/>: phase-5 M46-3) may name only
        /// environment variables with this prefix. The host sets it to its legacy API's own limits (Observability's <c>Webhooks:*</c>); unset
        /// refuses them, and the API's limits never apply to alias rows, nor these to the API's.
        /// </summary>
        public string? AliasSecretEnvPrefix { get; set; }
        /// <summary>A secret reference an alias writes may name only files directly in this folder; unset refuses files.</summary>
        public string? AliasSecretDirectory { get; set; }

        /// <summary>
        /// The SMTP servers email integrations may use, by name. Servers are part of the deployment: an integration names one, so the API can't
        /// point a connection at an arbitrary host.
        /// </summary>
        public Dictionary<string, SmtpServer> SmtpServers { get; set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Dry run (phase-5-plan I67, Notifications:Pipeline=shadow): deliveries planned while this is true are marked DryRun; the dispatcher takes
        /// them through every check to the provider call, then records WouldSend with the rendered body and sends nothing. Read at planning, not
        /// at dispatch: a backlog planned dry stays dry after the switch, so nothing another pipeline already sent goes out twice.
        /// </summary>
        public bool DryRun { get; set; }
    }

    public enum SmtpSecurity { None, StartTls, SslOnConnect }

    public sealed class SmtpServer
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 587;
        public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
        public string? Username { get; set; }
        /// <summary>A secret reference (<c>env:</c>, <c>file:</c>) to the password.</summary>
        public string? PasswordRef { get; set; }
    }

    /// <summary>Secrets are referenced, never stored (design D15), and read when sending.</summary>
    public static class SecretReferences
    {
        public const string Vault = "vault:";
        public const string KeyVault = "keyvault:";

        /// <summary>
        /// A reference in one of its forms: <c>env:NAME</c>, <c>file:/path</c>, <c>vault:path#field</c> (HashiCorp Vault KV v2) or
        /// <c>keyvault:name</c> (Azure Key Vault: letters, digits and dashes).
        /// </summary>
        public static bool IsValid(string reference)
            => IsLocal(reference)
            || reference.StartsWith(Vault, StringComparison.Ordinal) && reference.IndexOf('#') is var hash && hash > Vault.Length && hash < reference.Length - 1
                && !reference.Contains("..", StringComparison.Ordinal)
            || reference.StartsWith(KeyVault, StringComparison.Ordinal) && reference.Length is > 9 and <= 136
                && reference[KeyVault.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

        /// <summary>A reference read from the host itself (<c>env:</c>, <c>file:</c>): what the deployment's own keys, read at start, may name.</summary>
        public static bool IsLocal(string reference)
            => reference.StartsWith("env:", StringComparison.Ordinal) && reference.Length > 4
            || reference.StartsWith("file:", StringComparison.Ordinal) && reference.Length > 5;

        /// <summary>
        /// A reference set through the API, stricter than one in the deployment's own configuration: otherwise an admin could name any environment
        /// variable or file on the host (the database password) and have it sent somewhere as a signing key, a password or an address. Only
        /// variables with the configured prefix, or files directly in the configured folder (ported from Juice.Observability).
        /// </summary>
        public static bool IsAllowedFromApi(string reference, DeliveryOptions options)
        {
            if (!IsValid(reference))
            {
                return false;
            }
            if (reference.StartsWith("env:", StringComparison.Ordinal))
            {
                return options.ApiSecretEnvPrefix is { Length: > 0 } prefix && reference[4..].StartsWith(prefix, StringComparison.Ordinal)
                    && reference.Length > 4 + prefix.Length;
            }
            if (reference.StartsWith(Vault, StringComparison.Ordinal))
            {
                return options.ApiVaultPrefix is { Length: > 0 } prefix && reference[Vault.Length..].StartsWith(prefix, StringComparison.Ordinal)
                    && reference[Vault.Length..].IndexOf('#') > prefix.Length;
            }
            if (reference.StartsWith(KeyVault, StringComparison.Ordinal))
            {
                return options.ApiKeyVaultPrefix is { Length: > 0 } prefix && reference[KeyVault.Length..].StartsWith(prefix, StringComparison.Ordinal)
                    && reference.Length > KeyVault.Length + prefix.Length;
            }
            if (options.ApiSecretDirectory is not { Length: > 0 } directory)
            {
                return false;
            }
            return InFolder(reference[5..], directory);
        }

        /// <summary>
        /// A reference a producer's compatibility alias writes (M46-3): an environment variable with <see cref="DeliveryOptions.AliasSecretEnvPrefix"/>,
        /// or a file directly in <see cref="DeliveryOptions.AliasSecretDirectory"/>, as its legacy API allowed. Nothing when they are unset, and
        /// never a vault: legacy had none.
        /// </summary>
        public static bool IsAllowedFromAlias(string reference, DeliveryOptions options)
        {
            if (!IsLocal(reference))
            {
                return false;
            }
            if (reference.StartsWith("env:", StringComparison.Ordinal))
            {
                return options.AliasSecretEnvPrefix is { Length: > 0 } prefix && reference[4..].StartsWith(prefix, StringComparison.Ordinal)
                    && reference.Length > 4 + prefix.Length;
            }
            return options.AliasSecretDirectory is { Length: > 0 } directory && InFolder(reference[5..], directory);
        }

        /// <summary>
        /// Why the API's and the alias's secret limits overlap, or null when they don't (M46-3 review): one env prefix starting with the other, or
        /// the same folder, would let one writer name the other's secrets (an API admin, the alias's). The host fails to start on it.
        /// </summary>
        public static string? ScopeOverlap(DeliveryOptions options)
        {
            if (options.ApiSecretEnvPrefix is { Length: > 0 } api && options.AliasSecretEnvPrefix is { Length: > 0 } alias
                && (api.StartsWith(alias, StringComparison.Ordinal) || alias.StartsWith(api, StringComparison.Ordinal)))
            {
                return $"Delivery:ApiSecretEnvPrefix ({api}) and Delivery:AliasSecretEnvPrefix ({alias}) overlap: one starts with the other, so one writer could name the other's secrets. Give them distinct prefixes.";
            }
            if (options.ApiSecretDirectory is { Length: > 0 } apiFolder && options.AliasSecretDirectory is { Length: > 0 } aliasFolder
                && string.Equals(Folder(apiFolder), Folder(aliasFolder), StringComparison.OrdinalIgnoreCase))
            {
                return $"Delivery:ApiSecretDirectory and Delivery:AliasSecretDirectory are the same folder ({apiFolder}), so one writer could name the other's secrets. Give them distinct folders.";
            }
            return null;
        }

        private static string Folder(string directory) => Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static bool InFolder(string path, string directory)
            => Path.IsPathRooted(path) && !path.Contains("..", StringComparison.Ordinal)
                && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);

        /// <summary>The value of an <c>env:</c> or <c>file:</c> secret; throws when it isn't set. A vault's goes through <see cref="ISecretResolver"/>.</summary>
        public static string Resolve(string reference)
        {
            if (!IsLocal(reference) && IsValid(reference))
            {
                throw new InvalidOperationException($"The secret '{reference}' is in a vault, read through the secret resolver; it isn't read here.");
            }
            var value = reference switch
            {
                _ when reference.StartsWith("env:", StringComparison.Ordinal) => Environment.GetEnvironmentVariable(reference[4..]),
                _ when reference.StartsWith("file:", StringComparison.Ordinal) && File.Exists(reference[5..]) => File.ReadAllText(reference[5..]).Trim(),
                _ => null,
            };
            return string.IsNullOrEmpty(value) ? throw new InvalidOperationException($"The secret '{reference}' is not set.") : value;
        }
    }
}
