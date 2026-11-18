using System.Security.Claims;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Correlations;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// A producer's correlations (design §5.3, phase-3-plan M20): always its own application's (D5); another application's key is not found. A
    /// disabled application may still acknowledge, resolve and cancel: stopping sends can't wait. A key with <c>/</c> in it is sent encoded
    /// (<c>%2F</c>).
    /// </summary>
    internal static class CorrelationEndpoints
    {
        public static void MapCorrelationEndpoints(this IEndpointRouteBuilder app)
        {
            var correlations = app.MapGroup("/api/v1/tenants/{tenantId:guid}/correlations").WithTags("Correlations");
            correlations.MapGet("/{key}", async (Guid tenantId, string key, HttpContext http, ClaimsPrincipal user, Producers producers, CorrelationLifecycle lifecycle, CancellationToken ct) =>
            {
                var (caller, refusal) = await producers.ResolveAsync(user, ct, forWriting: false);
                if (caller is null)
                {
                    return refusal!;
                }
                return await lifecycle.GetAsync(tenantId, caller.Id, Key(http, key), ct) is { } view
                    ? Results.Ok(ToResponse(view))
                    : ApiResults.NotFound($"There is no correlation {Key(http, key)}.");
            });
            correlations.MapPost("/{key}/acknowledge", (Guid tenantId, string key, HttpContext http, AcknowledgeCorrelationBody? body, ClaimsPrincipal user, Producers producers,
                CorrelationLifecycle lifecycle, CancellationToken ct)
                => ChangeAsync(user, producers, ct, caller => lifecycle.AcknowledgeAsync(tenantId, caller, Key(http, key), body?.By, ct)));
            correlations.MapPost("/{key}/resolve", (Guid tenantId, string key, HttpContext http, ResolveCorrelationBody? body, ClaimsPrincipal user, Producers producers,
                CorrelationLifecycle lifecycle, CancellationToken ct)
                => ChangeAsync(user, producers, ct, caller => lifecycle.ResolveAsync(tenantId, caller, Key(http, key), body?.Outcome, body?.By, ct)));
            correlations.MapPost("/{key}/cancel", (Guid tenantId, string key, HttpContext http, CancelCorrelationBody? body, ClaimsPrincipal user, Producers producers,
                CorrelationLifecycle lifecycle, CancellationToken ct)
                => ChangeAsync(user, producers, ct, caller => lifecycle.CancelAsync(tenantId, caller, Key(http, key), body?.By, ct)));
        }

        /// <summary>
        /// The key as the producer sent it, decoded once from the request's own target. The route value has been decoded already, all but
        /// <c>%2F</c>, so decoding it again would turn a key's own <c>%41</c> into <c>A</c>, and undoing only <c>%2F</c> can't tell a key's
        /// <c>%2F</c> (sent <c>%252F</c>) from a slash.
        /// </summary>
        private static string Key(HttpContext http, string key)
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

        private static async Task<IResult> ChangeAsync(ClaimsPrincipal user, Producers producers, CancellationToken ct,
            Func<Guid, Task<Outcome<CorrelationView>>> change)
        {
            var (caller, refusal) = await producers.ResolveAsync(user, ct, forWriting: false);
            if (caller is null)
            {
                return refusal!;
            }
            Outcome<CorrelationView> outcome;
            try
            {
                outcome = await change(caller.Id);
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
                v.Notifications.Select(n => new CorrelationNotificationResponse(n.Id, n.RequestId, n.Type, n.State, n.CreatedAt)).ToList(), v.Recipients, v.Deliveries);
    }
}
