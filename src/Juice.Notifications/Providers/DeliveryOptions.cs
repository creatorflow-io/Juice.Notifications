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

        /// <summary>
        /// The SMTP servers email integrations may use, by name. Servers are part of the deployment: an integration names one, so the API can't
        /// point a connection at an arbitrary host.
        /// </summary>
        public Dictionary<string, SmtpServer> SmtpServers { get; set; } = new(StringComparer.Ordinal);
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
        public static bool IsValid(string reference)
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
            if (options.ApiSecretDirectory is not { Length: > 0 } directory)
            {
                return false;
            }
            var path = reference[5..];
            return Path.IsPathRooted(path) && !path.Contains("..", StringComparison.Ordinal)
                && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
        }

        /// <summary>The secret's value; throws when it isn't set.</summary>
        public static string Resolve(string reference)
        {
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
