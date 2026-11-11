using System.Security.Claims;
using Juice.MediatR;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Api
{
    /// <summary>Problem details with a stable <c>code</c> (and <c>errors</c> for field problems), and commands answered from their outcome.</summary>
    internal static class ApiResults
    {
        public static IResult Problem(int status, string code, string detail, IReadOnlyList<FieldError>? errors = null)
        {
            var extensions = new Dictionary<string, object?> { ["code"] = code };
            if (errors is { Count: > 0 })
            {
                extensions["errors"] = errors.Select(e => new FieldErrorResponse(e.Pointer, e.Message)).ToList();
            }
            return Results.Problem(statusCode: status, detail: detail, extensions: extensions);
        }

        public static IResult NotFound(string message) => Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);

        public static IResult ConcurrentChange()
            => Problem(StatusCodes.Status409Conflict, ErrorCodes.ConcurrentChange, "Someone else changed this at the same time. Try again.");

        /// <summary>A failed outcome as its problem; null for a success.</summary>
        public static IResult? Failure<T>(Outcome<T> outcome) => outcome.Kind switch
        {
            OutcomeKind.NotFound => NotFound(outcome.Message!),
            OutcomeKind.Conflict => Problem(StatusCodes.Status409Conflict, outcome.Code!, outcome.Message!, outcome.Errors),
            OutcomeKind.Invalid => Problem(StatusCodes.Status400BadRequest, outcome.Code!, outcome.Message!, outcome.Errors),
            OutcomeKind.Unprocessable => Problem(StatusCodes.Status422UnprocessableEntity, outcome.Code!, outcome.Message!, outcome.Errors),
            OutcomeKind.Forbidden => Problem(StatusCodes.Status403Forbidden, outcome.Code!, outcome.Message!),
            _ => null,
        };

        /// <summary>
        /// Sends one command for one HTTP request. A race with another transaction is answered by <paramref name="onConflict"/> from the constraint that
        /// stopped it; when that returns null the race isn't one this endpoint knows, and the exception goes on to the error handler (500). The
        /// request owns its scope and sends only this command, so after a race its DbContext can be cleared and read from.
        /// </summary>
        public static async Task<IResult> SendAsync<T>(IMediator mediator, NotificationsDbContext db, IRequest<Outcome<T>> command,
            Func<Outcome<T>, IResult> onSuccess, Func<DbConflict, Task<IResult?>> onConflict, CancellationToken ct)
        {
            Outcome<T> outcome;
            try
            {
                outcome = await mediator.Send(command, ct);
            }
            catch (Exception ex) when (DbConflicts.Classify(ex) is { } conflict)
            {
                db.ChangeTracker.Clear();
                if (await onConflict(conflict) is { } answer)
                {
                    return answer;
                }
                throw;
            }
            return Failure(outcome) ?? onSuccess(outcome);
        }
    }

    /// <summary>
    /// The application behind a producer's token: the token's <c>azp</c> (the OIDC client) is an application's <c>ClientId</c> (phase-1-plan I11).
    /// </summary>
    internal sealed class Producers(NotificationsDbContext db, Microsoft.Extensions.Options.IOptions<AuthOptions> auth)
    {
        /// <summary>The caller's application, or the problem to answer with.</summary>
        public async Task<(Application? Application, IResult? Refusal)> ResolveAsync(ClaimsPrincipal user, CancellationToken ct, bool forWriting = true)
        {
            // A producer is a client acting as itself (client credentials, phase-1-plan I11), never a person signed in through that client.
            // How a token shows that depends on the identity provider (AuthOptions.ProducerProof).
            var azp = user.FindFirst("azp")?.Value;
            var proof = auth.Value.ProducerProof == ProducerProofs.SubjectIsClient ? user.FindFirst("sub")?.Value : user.FindFirst("client_id")?.Value;
            var client = azp is not null && proof == azp ? azp : null;
            var app = client is null ? null : await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.ClientId == client, ct);
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
    }
}
