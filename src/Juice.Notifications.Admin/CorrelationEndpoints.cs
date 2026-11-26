using System.Security.Claims;
using Juice.Notifications.Admin;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Correlations;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// A producer's correlations (design §5.3, phase-3-plan M20): always its own application's (D5); another application's key is not found. A
    /// disabled application may still acknowledge, resolve and cancel: stopping sends can't wait. A key with <c>/</c> in it is sent encoded
    /// (<c>%2F</c>). The tenant's admins (phase-4c-plan I52) name the application (<c>?application=</c>: keys are per application) and act as
    /// themselves: <c>by</c> is who they signed in as, whatever the body says.
    /// </summary>
    internal static class CorrelationEndpoints
    {
        public static void MapCorrelationEndpoints(this IEndpointRouteBuilder tenant)
        {
            // A producer's own correlation (no ?application=, through the service's hook), or the one an admin names: reading it is Read, acting on
            // it Configure (m44-architecture §11 Q6).
            var correlations = tenant.MapGroup("/correlations").WithTags("Correlations").WithMetadata(new AdminPageMetadata(AdminPages.Correlations))
                .WithMetadata(new ProducerAlsoMetadata());
            correlations.MapGet("/{key}", async (Guid tenantId, string key, string? application, HttpContext http, ClaimsPrincipal user, Owners owners,
                CorrelationLifecycle lifecycle, CancellationToken ct) =>
            {
                var (owner, refusal) = await owners.ResolveAsync(tenantId, application, user, ct);
                if (owner is null)
                {
                    return refusal!;
                }
                return await lifecycle.GetAsync(tenantId, owner.Value.ApplicationId, Key(http, key), ct) is { } view
                    ? Results.Ok(ToResponse(view))
                    : ApiResults.NotFound($"There is no correlation {Key(http, key)}.");
            });
            correlations.MapPost("/{key}/acknowledge", (Guid tenantId, string key, string? application, HttpContext http, AcknowledgeCorrelationBody? body,
                ClaimsPrincipal user, Owners owners, CorrelationLifecycle lifecycle, CancellationToken ct)
                => ChangeAsync(tenantId, application, user, owners, ct,
                    owner => lifecycle.AcknowledgeAsync(tenantId, owner.ApplicationId, Key(http, key), owner.By ?? body?.By, ct)));
            correlations.MapPost("/{key}/resolve", (Guid tenantId, string key, string? application, HttpContext http, ResolveCorrelationBody? body,
                ClaimsPrincipal user, Owners owners, CorrelationLifecycle lifecycle, CancellationToken ct)
                => ChangeAsync(tenantId, application, user, owners, ct,
                    owner => lifecycle.ResolveAsync(tenantId, owner.ApplicationId, Key(http, key), body?.Outcome, owner.By ?? body?.By, ct)));
            correlations.MapPost("/{key}/cancel", (Guid tenantId, string key, string? application, HttpContext http, CancelCorrelationBody? body,
                ClaimsPrincipal user, Owners owners, CorrelationLifecycle lifecycle, CancellationToken ct)
                => ChangeAsync(tenantId, application, user, owners, ct,
                    owner => lifecycle.CancelAsync(tenantId, owner.ApplicationId, Key(http, key), owner.By ?? body?.By, ct)));
        }

        /// <summary>Whose correlation, and who acts: a producer's own (<c>By</c> null: its body names them), or for an admin the one named.</summary>
        internal readonly record struct Owner(Guid ApplicationId, string? By);

        internal sealed class Owners(INotificationsAdminAccess access, History.Investigation investigation, IAdminFallbackCaller? producers = null)
        {
            public async Task<(Owner? Owner, IResult? Refusal)> ResolveAsync(Guid tenantId, string? application, ClaimsPrincipal user, CancellationToken ct)
            {
                if (application is null)
                {
                    // The producer's own: the service's hook says which application the caller is. Embedded, there are no HTTP producers.
                    var (caller, refusal) = producers is null ? (null, null) : await producers.ResolveAsync(user, ct);
                    if (caller is not null)
                    {
                        return (new Owner(caller.Value, null), null);
                    }
                    // An admin who left the application out is told what's missing (#14 review), in both modes; anyone else is refused as before.
                    if (await access.RightsAsync(user, tenantId, ct) != AdminRights.None)
                    {
                        return (null, ApiResults.Problem(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "Name the correlation's application.",
                            [new FieldError("/application", "The tenant's admins name the application (?application=): keys are per application.")]));
                    }
                    return (null, refusal ?? ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotTenantAdmin, "The caller doesn't administer this tenant."));
                }
                if (await access.RightsAsync(user, tenantId, ct) == AdminRights.None)
                {
                    return (null, ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotTenantAdmin,
                        "Only the tenant's admins name the application; a producer reads its own correlations."));
                }
                return await investigation.LinkedApplicationAsync(tenantId, application, ct) is { } id
                    ? (new Owner(id, AdminCaller.Name(user) ?? "admin"), null)
                    : (null, ApiResults.NotFound($"There is no application {application} in this tenant."));
            }
        }

        /// <summary>
        /// The key as the producer sent it, decoded once from the request's own target. The route value has been decoded already, all but
        /// <c>%2F</c>, so decoding it again would turn a key's own <c>%41</c> into <c>A</c>, and undoing only <c>%2F</c> can't tell a key's
        /// <c>%2F</c> (sent <c>%252F</c>) from a slash.
        /// </summary>
        internal static string Key(HttpContext http, string key)
        {
            var raw = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
            const string marker = "/correlations/";
            var at = raw?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (raw is null || at < 0)
            {
                // No raw target (a test server): the route value with the one sequence routing leaves encoded.
                return key.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
            }
            var segment = raw[(at + marker.Length)..];
            var end = segment.IndexOfAny(['/', '?']);
            return Uri.UnescapeDataString(end < 0 ? segment : segment[..end]);
        }

        private static async Task<IResult> ChangeAsync(Guid tenantId, string? application, ClaimsPrincipal user, Owners owners, CancellationToken ct,
            Func<Owner, Task<Outcome<CorrelationView>>> change)
        {
            var (owner, refusal) = await owners.ResolveAsync(tenantId, application, user, ct);
            if (owner is null)
            {
                return refusal!;
            }
            Outcome<CorrelationView> outcome;
            try
            {
                outcome = await change(owner.Value);
            }
            catch (Exception ex) when (Data.DbConflicts.Classify(ex) is { Kind: Data.ConflictKind.RowGone or Data.ConflictKind.Aborted })
            {
                // A request touched it at the same moment: asking again answers on what it is now.
                return ApiResults.ConcurrentChange();
            }
            return ApiResults.Failure(outcome) ?? Results.Ok(ToResponse(outcome.Value!));
        }

        internal static CorrelationResponse ToResponse(CorrelationView v)
            => new(v.Key, v.State, v.AcknowledgedBy, v.AcknowledgedAt, v.ResolvedAt, v.ResolvedBy, v.Outcome, v.ReopenedAt, v.CreatedAt, v.LastRequestAt,
                v.Notifications.Select(n => new CorrelationNotificationResponse(n.Id, n.RequestId, n.Type, n.State, n.CreatedAt)).ToList(), v.Recipients, v.Deliveries,
                v.HeldAt, v.HoldReason, v.HeldBy);
    }
}
