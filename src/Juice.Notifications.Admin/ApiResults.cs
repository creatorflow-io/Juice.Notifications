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
                // As dictionaries: their keys are written as they are, whatever naming policy the host's JSON options have (an embedded host's
                // admin, M44-3), and they read as FieldErrorResponse.
                extensions["errors"] = errors.Select(e => new Dictionary<string, string> { ["pointer"] = e.Pointer, ["message"] = e.Message }).ToList();
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
            OutcomeKind.TooManyRequests => Problem(StatusCodes.Status429TooManyRequests, outcome.Code!, outcome.Message!),
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
}
