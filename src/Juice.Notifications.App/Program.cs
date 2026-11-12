using Juice.Notifications;
using Juice.Notifications.Api;
using Juice.Notifications.Data.PostgreSQL;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddJuiceNotifications(builder.Configuration).AddPostgresClaimStore();

// Metrics and traces (design §14) over OTLP, when the deployment names a collector (OTEL_EXPORTER_OTLP_ENDPOINT, and the other standard
// OTEL_* variables). Traces follow a request from the producer's traceparent through the stages to the provider call (System.Net.Http).
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "juice-notifications"))
        .WithTracing(tracing => tracing.AddSource(Telemetry.Name).AddSource("System.Net.Http").AddAspNetCoreInstrumentation())
        .WithMetrics(metrics => metrics.AddMeter(Telemetry.Name).AddAspNetCoreInstrumentation().AddMeter("System.Net.Http"))
        .UseOtlpExporter();
}

var app = builder.Build();
app.UseJuiceNotifications();
app.MapJuiceNotifications();
app.Run();

/// <summary>The host's entry point, visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;
