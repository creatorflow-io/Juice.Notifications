using Juice.MediatR;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// A tenant's delivery setup: integrations and their targets, routes, the outbound allowlist, and the resolvers for people. For platform admins and the tenant's own admins
    /// (<see cref="ITenantAccess"/>).
    /// </summary>
    internal static class ConfigurationEndpoints
    {
        public static void MapConfigurationEndpoints(this IEndpointRouteBuilder app)
        {
            var tenant = app.MapGroup("/api/v1/tenants/{tenantId:guid}").RequireTenantAdmin().WithTags("Tenant configuration");

            tenant.MapGet("/integrations", async (Guid tenantId, ConfigurationReader reader, CancellationToken ct)
                => Results.Ok((await reader.IntegrationsAsync(tenantId, ct)).Select(ToResponse)));
            tenant.MapPut("/integrations/{key}", (Guid tenantId, string key, PutIntegrationBody body, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => SendAsync(mediator, db, new PutIntegration(tenantId, key, body.Channel, body.Kind, body.Settings, body.SecretRef, body.Priority, body.Enabled), ToResponse, ct));
            tenant.MapDelete("/integrations/{key}", (Guid tenantId, string key, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => DeleteAsync(mediator, db, new DeleteIntegration(tenantId, key), ct));
            // A provider told where its callbacks go (Telegram's setWebhook; phase-4-plan M28): the deployment's public URL for this integration.
            tenant.MapPost("/integrations/{key}/callbacks", async (Guid tenantId, string key, NotificationsDbContext db, IEnumerable<INotificationProvider> providers,
                IEnumerable<ICallbackHandler> handlers, Microsoft.Extensions.Options.IOptions<Callbacks.CallbackOptions> callbacks, Providers.ISecretResolver secrets,
                CancellationToken ct) =>
            {
                var integration = await db.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Key == key, ct);
                if (integration is null)
                {
                    return ApiResults.NotFound($"There is no integration '{key}'.");
                }
                if (providers.FirstOrDefault(p => p.Channel == integration.Channel && p.Kind == integration.Kind) is not ICallbackRegistration registration)
                {
                    return ApiResults.Problem(StatusCodes.Status422UnprocessableEntity, ErrorCodes.CallbacksNotRegistered,
                        $"A {integration.Kind} integration's callbacks are set up in the provider's own console (see docs/providers.md).");
                }
                // The path segment is the provider's handler's: what the callback endpoint finds it by.
                var path = handlers.FirstOrDefault(h => h.Handles(integration.Kind))?.Provider ?? integration.Kind;
                if (callbacks.Value.UrlFor(path, integration.Id) is not { } url)
                {
                    return ApiResults.Problem(StatusCodes.Status422UnprocessableEntity, ErrorCodes.PublicUrlUnset,
                        "The deployment's Callbacks:PublicUrl isn't set, so there is no address to give the provider.");
                }
                string? secret;
                try
                {
                    secret = integration.SecretRef is { } reference ? await secrets.ResolveAsync(reference, ct) : null;
                }
                catch (InvalidOperationException ex)
                {
                    return ApiResults.Problem(StatusCodes.Status422UnprocessableEntity, ErrorCodes.ProviderRefused, ex.Message);
                }
                var refusal = await registration.RegisterCallbacksAsync(new IntegrationContext(integration.Id, integration.Key, integration.Kind, integration.Settings,
                    secret), url, ct);
                return refusal is null ? Results.NoContent() : ApiResults.Problem(StatusCodes.Status502BadGateway, ErrorCodes.ProviderRefused, refusal);
            });
            tenant.MapPut("/integrations/{key}/targets/{targetKey}", (Guid tenantId, string key, string targetKey, PutTargetBody body, IMediator mediator,
                NotificationsDbContext db, CancellationToken ct)
                => SendAsync(mediator, db, new PutTarget(tenantId, key, targetKey, body.Address, body.AddressRef, body.Enabled), ToResponse, ct));
            tenant.MapDelete("/integrations/{key}/targets/{targetKey}", (Guid tenantId, string key, string targetKey, IMediator mediator, NotificationsDbContext db,
                CancellationToken ct)
                => DeleteAsync(mediator, db, new DeleteTarget(tenantId, key, targetKey), ct));

            tenant.MapGet("/routes", async (Guid tenantId, ConfigurationReader reader, CancellationToken ct)
                => Results.Ok((await reader.RoutesAsync(tenantId, ct)).Select(ToResponse)));
            tenant.MapPut("/routes/{key}", (Guid tenantId, string key, PutRouteBody body, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => SendAsync(mediator, db, new PutRoute(tenantId, key, body.Match, body.Targets, body.Enabled), ToResponse, ct));
            tenant.MapDelete("/routes/{key}", (Guid tenantId, string key, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => DeleteAsync(mediator, db, new DeleteRoute(tenantId, key), ct));

            // The tenant's own templates (phase-4-plan I45): over the application's, for each type's latest version.
            tenant.MapGet("/types", async (Guid tenantId, Types.TenantTemplateReader reader, CancellationToken ct)
                => Results.Ok((await reader.TypesAsync(tenantId, ct)).Select(t => new TenantTypeResponse(t.Key, t.Name, t.Version, t.Application,
                    t.Channels.Select(c => new TenantTypeChannelResponse(c.Channel, c.Declared, c.Overridden)).ToList()))));
            tenant.MapGet("/types/{key}/templates/{channel}", async (Guid tenantId, string key, string channel, string? locale, Types.TenantTemplateReader reader,
                CancellationToken ct) =>
            {
                var found = await reader.GetAsync(tenantId, key, channel, locale, ct);
                return ApiResults.Failure(found) ?? Results.Ok(ToResponse(found.Value!));
            });
            tenant.MapPut("/types/{key}/templates/{channel}", (Guid tenantId, string key, string channel, PutTemplateBody body, IMediator mediator,
                NotificationsDbContext db, CancellationToken ct)
                => SendAsync(mediator, db, new Types.PutTenantTemplate(tenantId, key, channel, body.Locale, body.Body), ToResponse, ct));
            tenant.MapDelete("/types/{key}/templates/{channel}", (Guid tenantId, string key, string channel, string? locale, IMediator mediator,
                NotificationsDbContext db, CancellationToken ct)
                => DeleteAsync(mediator, db, new Types.DeleteTenantTemplate(tenantId, key, channel, locale), ct));
            tenant.MapPost("/templates/preview", async (Guid tenantId, PreviewBody body, Types.TenantTemplateReader reader, CancellationToken ct) =>
            {
                var preview = await reader.PreviewAsync(tenantId, body.Type, body.Channel, body.Body, body.Data, ct);
                return ApiResults.Failure(preview)
                    ?? Results.Ok(new PreviewResponse(preview.Value!.Channel, preview.Value.Content, preview.Value.Error, preview.Value.Data));
            });

            // The tenant's own settings (phase-4-plan §4) and its storms (I43).
            tenant.MapGet("/settings", async (Guid tenantId, TenantSettingsReader reader, CancellationToken ct)
                => Results.Ok(ToResponse(await reader.GetAsync(tenantId, ct))));
            tenant.MapPut("/settings", (Guid tenantId, TenantSettingsBody body, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => SendAsync(mediator, db, new PutTenantSettings(tenantId, body.StormThreshold, body.RequestRetentionDays, body.ContentRetentionDays), ToResponse, ct));
            tenant.MapGet("/storms", async (Guid tenantId, bool? all, TenantSettingsReader reader, CancellationToken ct)
                => Results.Ok((await reader.StormsAsync(tenantId, all ?? false, ct))
                    .Select(s => new StormResponse(s.Id, s.Route, s.StartedAt, s.EndedAt, s.Peak, s.Digested))));

            tenant.MapGet("/allowlist", async (Guid tenantId, ConfigurationReader reader, CancellationToken ct)
                => Results.Ok((await reader.AllowlistAsync(tenantId, ct)).Select(ToResponse)));
            tenant.MapPut("/allowlist", (Guid tenantId, PutAllowlistBody body, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => SendAsync(mediator, db, new PutAllowlistEntry(tenantId, body.Destination ?? "", body.Ports), ToResponse, ct));
            // A destination can be a network (10.20.0.0/16), so it travels in the query, not the path.
            tenant.MapDelete("/allowlist", (Guid tenantId, string destination, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => DeleteAsync(mediator, db, new DeleteAllowlistEntry(tenantId, destination), ct));

            tenant.MapGet("/resolvers", async (Guid tenantId, ConfigurationReader reader, CancellationToken ct)
                => Results.Ok((await reader.ResolversAsync(tenantId, ct)).Select(ToResponse)));
            tenant.MapPut("/resolvers/{key}", (Guid tenantId, string key, PutResolverBody body, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => SendAsync(mediator, db, new PutResolver(tenantId, key, body.Kind, body.Handles, body.Settings, body.SecretRef, body.Enabled), ToResponse, ct));
            tenant.MapDelete("/resolvers/{key}", (Guid tenantId, string key, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => DeleteAsync(mediator, db, new DeleteResolver(tenantId, key), ct));

            // Escalation policies (phase-3-plan M22).
            tenant.MapGet("/escalation-policies", async (Guid tenantId, ConfigurationReader reader, CancellationToken ct)
                => Results.Ok((await reader.EscalationPoliciesAsync(tenantId, ct)).Select(ToResponse)));
            tenant.MapPut("/escalation-policies/{key}", (Guid tenantId, string key, PutEscalationPolicyBody body, IMediator mediator, NotificationsDbContext db,
                CancellationToken ct)
                => SendAsync(mediator, db, new PutEscalationPolicy(tenantId, key, body.Steps, body.StopOnAcknowledge), ToResponse, ct));
            tenant.MapDelete("/escalation-policies/{key}", (Guid tenantId, string key, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => DeleteAsync(mediator, db, new DeleteEscalationPolicy(tenantId, key), ct));
        }

        /// <summary>
        /// A write that raced another: two admins creating the same key, a PUT whose row changed since it read it (the definition hash is a
        /// concurrency token), or a target added to an integration being deleted (foreign key). The loser asks again and sees the current state.
        /// </summary>
        private static TenantTemplateResponse ToResponse(Types.TenantTemplateView t)
            => new(t.Type, t.TypeVersion, t.Channel, t.Locale, t.Application, t.Tenant, t.TenantVersion, t.UpdatedAt);

        private static TenantSettingsResponse ToResponse(TenantSettingsView s) => new(s.StormThreshold, s.DefaultStormThreshold, s.RequestRetentionDays,
            s.DefaultRequestRetentionDays, s.ContentRetentionDays, s.DefaultContentRetentionDays, s.UpdatedAt);

        private static Task<IResult> SendAsync<TView, TResponse>(IMediator mediator, NotificationsDbContext db, IRequest<Outcome<TView>> command,
            Func<TView, TResponse> map, CancellationToken ct)
            => ApiResults.SendAsync(mediator, db, command, outcome => Results.Ok(map(outcome.Value!)),
                conflict => Task.FromResult<IResult?>(conflict switch
                {
                    { Kind: ConflictKind.Unique or ConflictKind.Aborted or ConflictKind.RowGone } => ApiResults.ConcurrentChange(),
                    // Only the race this endpoint can lose; any other foreign key failing is a bug, and stays a 500 that shows it.
                    { Kind: ConflictKind.ForeignKey, Constraint: Constraints.TargetIntegration } => ApiResults.ConcurrentChange(),
                    // A route naming a target deleted at the same moment (deferred #8), or held by a route deleted at the same moment.
                    { Kind: ConflictKind.ForeignKey, Constraint: Constraints.RouteTargetTarget or Constraints.RouteTargetRoute } => ApiResults.ConcurrentChange(),
                    _ => null,
                }), ct);

        /// <summary>
        /// A delete that raced: the row changed, a delivery started going through the integration before the commit, or a route started naming
        /// the target (or one of the integration's targets).
        /// </summary>
        private static Task<IResult> DeleteAsync(IMediator mediator, NotificationsDbContext db, IRequest<Outcome<bool>> command, CancellationToken ct)
            => ApiResults.SendAsync(mediator, db, command, _ => Results.NoContent(),
                conflict => Task.FromResult<IResult?>(conflict switch
                {
                    { Kind: ConflictKind.Aborted or ConflictKind.RowGone } => ApiResults.ConcurrentChange(),
                    { Kind: ConflictKind.ForeignKey, Constraint: Constraints.DeliveryIntegration or Constraints.AttemptIntegration
                        or Constraints.RouteTargetTarget } => ApiResults.ConcurrentChange(),
                    _ => null,
                }), ct);

        private static IntegrationResponse ToResponse(IntegrationView v)
            => new(v.Key, v.Channel, v.Kind, v.Settings, v.SecretRef, v.Priority, v.Enabled, v.ManagedBy, v.Targets.Select(ToResponse).ToList(), v.UpdatedAt);

        private static TargetResponse ToResponse(TargetView v) => new(v.Key, v.Address, v.AddressRef, v.Enabled, v.ManagedBy, v.UpdatedAt);

        private static RouteResponse ToResponse(RouteView v) => new(v.Key, v.Match, v.Targets, v.Enabled, v.ManagedBy, v.UpdatedAt);

        private static AllowlistResponse ToResponse(AllowlistView v) => new(v.Destination, v.Ports, v.ManagedBy, v.UpdatedAt);

        private static ResolverResponse ToResponse(ResolverView v) => new(v.Key, v.Kind, v.Handles, v.Settings, v.SecretRef, v.Enabled, v.ManagedBy, v.UpdatedAt);

        private static EscalationPolicyResponse ToResponse(EscalationPolicyView v) => new(v.Key, v.Steps, v.StopOnAcknowledge, v.ManagedBy, v.UpdatedAt);
    }
}
