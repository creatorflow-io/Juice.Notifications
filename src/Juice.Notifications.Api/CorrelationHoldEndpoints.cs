using System.Security.Claims;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Correlations;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// A producer's holds on its correlations (phase-5-plan p5-d3): <c>POST …/correlations/{key}/hold</c> and <c>…/release</c>, under the same
    /// group as <see cref="CorrelationEndpoints"/>, with the key decoded by the same rule. Producer-only: always the caller's own application
    /// (D5), with no <c>?application=</c> for tenant admins, since a release from the Console would undo the producer's suppression behind its back
    /// (m44-architecture §11 Q3). A disabled application may still hold and release. Both are idempotent by state: asking again answers 200 with
    /// the hold as it stands.
    /// </summary>
    internal static class CorrelationHoldEndpoints
    {
        public static void MapCorrelationHoldEndpoints(this IEndpointRouteBuilder app)
        {
            var correlations = app.MapGroup("/api/v1/tenants/{tenantId:guid}/correlations").WithTags("Correlations");
            correlations.MapPost("/{key}/hold", (Guid tenantId, string key, HttpContext http, HoldCorrelationBody? body, ClaimsPrincipal user,
                Producers producers, CorrelationLifecycle lifecycle, CancellationToken ct)
                => ChangeAsync(user, producers, ct,
                    application => lifecycle.HoldAsync(tenantId, application, CorrelationEndpoints.Key(http, key), body?.Reason, body?.By, ct)));
            correlations.MapPost("/{key}/release", (Guid tenantId, string key, HttpContext http, ReleaseCorrelationBody? body, ClaimsPrincipal user,
                Producers producers, CorrelationLifecycle lifecycle, CancellationToken ct)
                => ChangeAsync(user, producers, ct,
                    application => lifecycle.ReleaseAsync(tenantId, application, CorrelationEndpoints.Key(http, key), body?.By, ct)));
        }

        private static async Task<IResult> ChangeAsync(ClaimsPrincipal user, Producers producers, CancellationToken ct,
            Func<Guid, Task<Outcome<CorrelationHoldResult>>> change)
        {
            var (caller, refusal) = await producers.ResolveAsync(user, ct, forWriting: false);
            if (caller is null)
            {
                return refusal!;
            }
            Outcome<CorrelationHoldResult> outcome;
            try
            {
                outcome = await change(caller.Id);
            }
            catch (Exception ex) when (Data.DbConflicts.Classify(ex) is { Kind: Data.ConflictKind.RowGone or Data.ConflictKind.Aborted })
            {
                // A request, a resolve or another hold changed it at the same moment: asking again answers on what it is now.
                return ApiResults.ConcurrentChange();
            }
            return ApiResults.Failure(outcome) ?? Results.Ok(CorrelationEndpoints.ToResponse(outcome.Value!.View));
        }
    }
}
