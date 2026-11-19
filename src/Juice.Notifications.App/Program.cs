using Juice.Notifications;
using Juice.Notifications.Api;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Messaging;
using Juice.Notifications.Providers.Chat;
using Juice.Notifications.Providers.Email;
using Juice.Notifications.Providers.Push;
using Juice.Notifications.Providers.Sms;
using Juice.Notifications.Providers.Telegram;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddJuiceNotifications(builder.Configuration).AddPostgresClaimStore();
// NotificationRequested over RabbitMQ (phase-1-plan I5), when Intake:RabbitMQ:Enabled is true.
builder.Services.AddNotificationsRabbitMQIntake(builder.Configuration);
// Email through SES and SendGrid (phase-4-plan I39, M26), with their callbacks.
builder.Services.AddEmailProviders();
// Slack and Teams (phase-4-plan M27): the slack and teams channels, their providers, and the Slack app's callbacks.
builder.Services.AddChatProviders();
// Telegram (phase-4-plan M28): the telegram channel, the Bot API provider and the bot's webhook.
builder.Services.AddTelegram();
// SMS through Twilio (phase-4-plan M29), with its status callbacks.
builder.Services.AddSms();
// Push (phase-4-plan M30): FCM, APNs and Web Push to the devices people's apps register.
builder.Services.AddPush();

// Metrics and traces (design §14) over OTLP, when the deployment names a collector (OTEL_EXPORTER_OTLP_ENDPOINT, and the other standard
// OTEL_* variables). Traces follow a request from the producer's traceparent through the stages to the provider call.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "juice-notifications"))
        // Not the System.Net.Http spans: they record a webhook's full URL, which can be a secret (an addressRef target, D15). The dispatch span
        // names the integration instead, and HttpClient still carries the trace context on to the receiver.
        .WithTracing(tracing => tracing.AddSource(Telemetry.Name).AddAspNetCoreInstrumentation()
            // Juice's messaging spans: the RabbitMQ intake's consumer span, which continues the producer's trace.
            .AddSource(Juice.Messaging.Diagnostics.JuiceActivitySource.Name))
        // Not the System.Net.Http meter either: it tags every series with the webhook's host (server.address), part of the same secret.
        .WithMetrics(metrics => metrics.AddMeter(Telemetry.Name).AddAspNetCoreInstrumentation())
        .UseOtlpExporter();
}

var app = builder.Build();
// The Notifications Console (phase-4c-plan I49), when the image includes it (web root, `webroot` to move it): static files on the API's own
// origin, never framed. Hashed bundles are cached for good; the index page never is, so a deploy is picked up on the next load.
app.Use((context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    return next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles(ConsoleFiles.Options());
app.UseJuiceNotifications();
app.MapJuiceNotifications();
// Any other path outside /api, /hubs and /health, and not a file, is a Console route. API paths keep routing's own answers (404, 405), and a
// missing file is never answered with the index page (a stale chunk must fail, not load HTML as script).
app.MapFallbackToFile("{*path:nonfile:regex(^(?!(api|hubs|health)(/|$)).*$)}", "index.html", ConsoleFiles.Options()).AllowAnonymous();
app.Run();

/// <summary>
/// The Console's built files: Angular names its bundles with an 8-character content hash (<c>main-FIXYNYJB.js</c>, <c>chunk-Ce0mZ_Dd.js</c>)
/// and puts the media they name, hashed too, under <c>media/</c>. Those never change under their name; anything else (the index page,
/// config.json, logo.svg, an asset added to public/) may.
/// </summary>
internal static partial class ConsoleFiles
{
    /// <summary>For the files and for the fallback's index page alike.</summary>
    public static StaticFileOptions Options() => new()
    {
        OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = IsHashed(file.Context.Request.Path.Value ?? "")
            ? "public, max-age=31536000, immutable"
            : "no-cache",
    };

    /// <summary>A bundle (<c>.js</c>, <c>.mjs</c>, <c>.css</c> with its hash) or a file under <c>/media/</c>, by its request path.</summary>
    public static bool IsHashed(string path) => path.StartsWith("/media/", StringComparison.Ordinal) || Bundle().IsMatch(path);

    [System.Text.RegularExpressions.GeneratedRegex(@"-[A-Za-z0-9_-]{8}\.(js|mjs|css)$")]
    private static partial System.Text.RegularExpressions.Regex Bundle();
}
