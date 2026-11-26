namespace Juice.Notifications.Api
{
    /// <summary>
    /// A producer's holds on its correlations (phase-5-plan p5-d3): <c>POST …/correlations/{key}/hold</c> and <c>…/release</c>, under the same
    /// group as <see cref="CorrelationEndpoints"/>. Producer-only: always the caller's own application, with no <c>?application=</c> for tenant
    /// admins. The endpoints arrive with M44-1; until then nothing is mapped.
    /// </summary>
    internal static class CorrelationHoldEndpoints
    {
        public static void MapCorrelationHoldEndpoints(this IEndpointRouteBuilder app)
        {
        }
    }
}
