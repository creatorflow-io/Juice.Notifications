namespace Juice.Notifications.Api
{
    /// <summary>
    /// Configuration section <c>Cors</c>: the browser apps (origins) that may call the API and the inbox hub from another origin, such as an app
    /// using <c>@juice-js/notifications</c>. Empty, the default: no cross-origin calls. Tokens travel in the Authorization header (or, to the hub
    /// over WebSockets, the query), never in cookies, so credentials aren't allowed.
    /// </summary>
    public sealed class CorsSettings
    {
        public const string Section = "Cors";
        public const string Policy = "notifications-apps";

        /// <summary>Exact origins: <c>https://app.example.com</c> (scheme, host and port; no path, no wildcard).</summary>
        public List<string> Origins { get; set; } = [];

        /// <summary>The first origin that isn't one, or null when all are.</summary>
        internal string? Problem()
            => Origins.FirstOrDefault(o => !Uri.TryCreate(o, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.GetLeftPart(UriPartial.Authority) != o.TrimEnd('/') || o.Contains('*'));
    }

    internal static class CorsRegistration
    {
        public static IServiceCollection AddNotificationsCors(this IServiceCollection services, IConfiguration configuration)
        {
            var settings = configuration.GetSection(CorsSettings.Section).Get<CorsSettings>() ?? new CorsSettings();
            if (settings.Problem() is { } bad)
            {
                throw new InvalidOperationException($"Cors:Origins has '{bad}', which isn't an origin: https://app.example.com, no path or wildcard.");
            }
            services.AddSingleton(settings);
            services.AddCors(options => options.AddPolicy(CorsSettings.Policy, policy => policy
                .WithOrigins([.. settings.Origins.Select(o => o.TrimEnd('/'))])
                .AllowAnyHeader()
                .AllowAnyMethod()
                .WithExposedHeaders("Location", "Retry-After")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
            return services;
        }
    }
}
