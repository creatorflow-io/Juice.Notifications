using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Notifications").Get<NotificationSettings>() ?? new();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<NotificationStore>();
builder.Services.AddHttpClient("webhooks").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false
}).ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHostedService<DeliveryWorker>();
var app = builder.Build();

var tenants = app.MapGroup("/tenants/{tenantId}");
tenants.AddEndpointFilter(async (context, next) =>
{
    var tenantId = context.HttpContext.Request.RouteValues["tenantId"]?.ToString() ?? "";
    var supplied = context.HttpContext.Request.Headers["X-API-Key"].ToString();
    if (!settings.Tenants.TryGetValue(tenantId, out var tenant) || string.IsNullOrEmpty(tenant.ApiKey) ||
        string.IsNullOrEmpty(supplied) ||
        !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(tenant.ApiKey))))
        return Results.Unauthorized();
    return await next(context);
});

tenants.MapPut("/types/{name}", async (string tenantId, string name, NotificationType definition, NotificationStore store) =>
{
    if (!Regex.IsMatch(name, @"^[A-Za-z0-9._-]{1,100}$") || definition.Fields is null ||
        definition.Subject is null || definition.Body is null ||
        definition.Fields.Any(f => !Regex.IsMatch(f.Key, @"^[A-Za-z][A-Za-z0-9_]{0,63}$") ||
                                   f.Value is not ("string" or "number" or "boolean")))
        return Results.BadRequest("Invalid type definition.");

    var templates = definition.Subject + "\n" + definition.Body;
    if (Regex.Matches(templates, @"\{\{([^{}]+)\}\}").Any(m => !definition.Fields.ContainsKey(m.Groups[1].Value)))
        return Results.BadRequest("Templates reference an undeclared field.");

    await store.PutType(tenantId, name, definition);
    return Results.NoContent();
});

tenants.MapPost("/notifications", async (string tenantId, NotificationRequest request, NotificationStore store) =>
{
    if (string.IsNullOrEmpty(request.Type) || request.Data is null)
        return Results.BadRequest("Type and data are required.");

    var tenant = settings.Tenants[tenantId];
    if (tenant.Emails.Count == 0 && tenant.Webhooks.Count == 0)
        return Results.BadRequest("No destinations are configured for this tenant.");

    var result = await store.CreateNotification(tenantId, request, tenant);
    return result is null ? Results.BadRequest("Unknown type or invalid data.") :
        Results.Accepted($"/tenants/{Uri.EscapeDataString(tenantId)}/notifications/{result.Id}", result);
});

tenants.MapGet("/notifications/{id:guid}", async (string tenantId, Guid id, NotificationStore store) =>
{
    var notification = await store.GetNotification(tenantId, id);
    return notification is null ? Results.NotFound() : Results.Ok(notification);
});

tenants.MapGet("/notifications", async (string tenantId, NotificationStore store) =>
    Results.Ok(await store.GetHistory(tenantId)));

app.Run();
