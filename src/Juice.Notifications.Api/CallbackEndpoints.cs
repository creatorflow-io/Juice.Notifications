using System.Threading.RateLimiting;
using Juice.Notifications.Callbacks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// <c>POST /api/v1/callbacks/{provider}/{integrationId}</c> (phase-4-plan p4-d6, I40): where providers report deliveries, bounces, complaints
    /// and chat buttons. Anonymous: the integration's provider handler verifies each call (its signature against the integration's secret, or
    /// the provider's published keys). Answers carry nothing about the tenant: an unknown integration, a provider the integration doesn't use and
    /// a refused signature are <c>404</c>, <c>404</c> and <c>401</c> with no body; too many calls for one integration are <c>429</c>.
    /// </summary>
    internal static class CallbackEndpoints
    {
        private const string Limit = "callbacks";
        private const int MaxBody = 1024 * 1024;

        public static IServiceCollection AddCallbackLimits(this IServiceCollection services, IConfiguration configuration)
            => services.AddRateLimiter(limiter =>
            {
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                // Per integration (the path names it), so one tenant's noisy provider, or someone guessing, can't crowd out the others.
                limiter.AddPolicy(Limit, http =>
                {
                    var perMinute = http.RequestServices.GetRequiredService<IOptions<CallbackOptions>>().Value.PerMinute;
                    return RateLimitPartition.GetFixedWindowLimiter(http.Request.RouteValues["integrationId"]?.ToString() ?? "",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, perMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
                });
            });

        public static void MapCallbackEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/api/v1/callbacks/{provider}/{integrationId:guid}", async (string provider, Guid integrationId, HttpContext http,
                ProviderCallbacks callbacks, CancellationToken ct) =>
            {
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                {
                    size.MaxRequestBodySize = MaxBody;
                }
                if (http.Request.ContentLength > MaxBody)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }
                byte[] body;
                using (var buffer = new MemoryStream())
                {
                    try
                    {
                        await http.Request.Body.CopyToAsync(buffer, ct);
                    }
                    catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                    {
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    }
                    if (buffer.Length > MaxBody)
                    {
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    }
                    body = buffer.ToArray();
                }
                var headers = http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                var url = new Uri($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}{http.Request.Path}{http.Request.QueryString}");
                var answer = await callbacks.ReceiveAsync(provider, integrationId, new CallbackRequest(headers, body, url), ct);
                return answer.Outcome switch
                {
                    CallbackOutcome.Accepted when answer.Reply is { } reply => Results.Content(reply.Body, reply.ContentType, statusCode: reply.Status),
                    CallbackOutcome.Accepted => Results.Ok(),
                    CallbackOutcome.Refused => Results.StatusCode(StatusCodes.Status401Unauthorized),
                    CallbackOutcome.Unavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                    _ => Results.NotFound(),
                };
            }).AllowAnonymous().RequireRateLimiting(Limit).WithTags("Callbacks");
        }
    }
}
