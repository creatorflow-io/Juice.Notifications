using System.Security.Claims;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// The application behind a producer's token: the token's <c>azp</c> (the OIDC client) is an application's <c>ClientId</c> (phase-1-plan I11).
    /// </summary>
    internal sealed class Producers(NotificationsDbContext db, Microsoft.Extensions.Options.IOptions<AuthOptions> auth)
    {
        /// <summary>The caller's application, or the problem to answer with.</summary>
        public async Task<(Application? Application, IResult? Refusal)> ResolveAsync(ClaimsPrincipal user, CancellationToken ct, bool forWriting = true)
        {
            // A producer is a client acting as itself (client credentials, phase-1-plan I11), never a person signed in through that client.
            var client = ActingClient(user, auth.Value);
            // The service's own application (contact verification) is never a producer, whatever client a token names.
            var app = client is null ? null : await db.Applications.AsNoTracking()
                .SingleOrDefaultAsync(a => a.ClientId == client && a.Key != People.ServiceTypes.ApplicationKey, ct);
            if (app is null)
            {
                return (null, ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotAProducer,
                    "The token is not a registered application's client-credentials token. A platform admin registers the client id; producers authenticate as their client, not as a user."));
            }
            if (forWriting && !app.Enabled)
            {
                return (null, ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.ApplicationDisabled, $"The application {app.Key} is disabled."));
            }
            return (app, null);
        }

        /// <summary>
        /// The client a token was issued to for itself (client credentials), or null for a person's token. How a token shows that depends on the
        /// identity provider (<see cref="AuthOptions.ProducerProof"/>): producers must be one, and <c>me</c> refuses one (a service account is no person).
        /// </summary>
        public static string? ActingClient(ClaimsPrincipal user, AuthOptions auth)
        {
            var azp = user.FindFirst("azp")?.Value;
            var proof = auth.ProducerProof == ProducerProofs.SubjectIsClient ? user.FindFirst("sub")?.Value : user.FindFirst("client_id")?.Value;
            return azp is not null && proof == azp ? azp : null;
        }
    }
}
