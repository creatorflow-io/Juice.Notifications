using System.Diagnostics;
using Juice.Messaging;
using Juice.Messaging.Diagnostics;
using Juice.Notifications.Data;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// Starts the Juice message context for each HTTP request, with the caller's <c>x-correlation-id</c> (or a new one), tags the server span with
    /// it, and echoes the id on the response, so messages a command publishes carry the caller's correlation. The same contract as Juice.AspNetCore's
    /// MessageContextMiddleware, without that package's dependencies (Swashbuckle, GraphQL, Identity.Web).
    /// </summary>
    internal static class MessageContextMiddleware
    {
        public const string Header = "x-correlation-id";
        private const int MaxLength = 128;

        /// <summary>
        /// A caller's id is kept only if it is 1–128 printable ASCII characters without spaces. Kestrel accepts control characters in a request header
        /// but refuses them in a response header, so echoing one would fail the request; it would also reach logs and published messages.
        /// </summary>
        internal static bool IsUsable(string? id) => id is { Length: > 0 and <= MaxLength } && id.All(c => c is >= '\x21' and <= '\x7E');

        /// <summary>Register it before the exception handler, so error responses and error logs keep the correlation too.</summary>
        public static IApplicationBuilder UseNotificationsMessageContext(this IApplicationBuilder app)
            => app.Use(async (context, next) =>
            {
                var given = context.Request.Headers[Header].FirstOrDefault();
                using var scope = NotificationsMessageContext.Begin("api", IsUsable(given) ? given! : Guid.NewGuid().ToString());
                // The id in effect: a context that was already active wins over the header, and published messages carry this one.
                var correlationId = MessageContext.Current.CorrelationId;
                MessagingActivity.EnrichWithMessageContext(Activity.Current);
                // Set when the response starts, so it survives the exception handler clearing the response for a problem.
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[Header] = correlationId;
                    return Task.CompletedTask;
                });
                await next(context);
            });
    }
}
