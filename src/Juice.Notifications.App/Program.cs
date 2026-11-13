using Juice.Notifications;
using Juice.Notifications.Api;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Messaging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddJuiceNotifications(builder.Configuration).AddPostgresClaimStore();
// NotificationRequested over RabbitMQ (phase-1-plan I5), when Intake:RabbitMQ:Enabled is true.
builder.Services.AddNotificationsRabbitMQIntake(builder.Configuration);

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
app.UseJuiceNotifications();
app.MapJuiceNotifications();
app.Run();

/// <summary>The host's entry point, visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;
