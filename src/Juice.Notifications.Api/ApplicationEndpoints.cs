using Juice.MediatR;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Applications;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Api
{
    /// <summary>Applications and the tenants they send for: platform admins only.</summary>
    internal static class ApplicationEndpoints
    {
        public static void MapApplicationEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/v1/applications").RequireAuthorization(Policies.PlatformAdmin).WithTags("Applications");
            group.MapGet("", async (ApplicationReader reader, CancellationToken ct) => Results.Ok((await reader.ListAsync(ct)).Select(ToResponse)));
            group.MapGet("/{key}", async (string key, ApplicationReader reader, CancellationToken ct)
                => await reader.GetAsync(key, ct) is { } view ? Results.Ok(ToResponse(view)) : NotFound($"There is no application '{key}'."));
            group.MapPost("", async (CreateApplicationBody body, Sender sender, CancellationToken ct)
                => await sender.SendAsync(new CreateApplication(body.Key, body.Name, body.ClientId, body.BrokerUser), body.Key, ct, view => $"/api/v1/applications/{view.Key}"));
            group.MapPut("/{key}", async (string key, UpdateApplicationBody body, Sender sender, CancellationToken ct)
                => await sender.SendAsync(new UpdateApplication(key, body.Name, body.ClientId, body.Enabled, body.BrokerUser), key, ct));
            group.MapPut("/{key}/tenants/{tenantId:guid}", async (string key, Guid tenantId, Sender sender, CancellationToken ct)
                => await sender.SendAsync(new LinkTenant(key, tenantId), key, ct));
            group.MapDelete("/{key}/tenants/{tenantId:guid}", async (string key, Guid tenantId, Sender sender, CancellationToken ct)
                => await sender.SendAsync(new UnlinkTenant(key, tenantId), key, ct));
        }

        private static ApplicationResponse ToResponse(ApplicationView v) => new(v.Id, v.Key, v.Name, v.ClientId, v.BrokerUser, v.Enabled, v.Tenants, v.CreatedAt, v.UpdatedAt);

        /// <summary>
        /// Sends one command for one HTTP request, and answers a race with another transaction from the constraint that stopped it. Nothing runs
        /// again: each race here has one right answer. The request owns its scope and sends only this command, so after a race its DbContext can be
        /// cleared and read from.
        /// </summary>
        internal sealed class Sender(IMediator mediator, NotificationsDbContext db, ApplicationReader reader)
        {
            public async Task<IResult> SendAsync(IRequest<Outcome<ApplicationView>> command, string key, CancellationToken ct,
                Func<ApplicationView, string>? location = null)
            {
                Outcome<ApplicationView> outcome;
                try
                {
                    outcome = await mediator.Send(command, ct);
                }
                catch (Exception ex) when (DbConflicts.Classify(ex) is { } conflict && Answer(command, conflict) is var answer && answer != Race.Unknown)
                {
                    db.ChangeTracker.Clear();
                    return await AnswerAsync(answer, key, (command as CreateApplication)?.ClientId, ct);
                }
                return outcome.Kind switch
                {
                    OutcomeKind.Created => Results.Created(location?.Invoke(outcome.Value!), ToResponse(outcome.Value!)),
                    OutcomeKind.Ok => Results.Ok(ToResponse(outcome.Value!)),
                    OutcomeKind.NotFound => NotFound(outcome.Message!),
                    OutcomeKind.Conflict => Problem(StatusCodes.Status409Conflict, outcome.Code!, outcome.Message!),
                    _ => Problem(StatusCodes.Status400BadRequest, outcome.Code!, outcome.Message!),
                };
            }

            /// <summary>
            /// Which race this was, from the command and the constraint that stopped it. <see cref="Race.Unknown"/> is not a race this API knows:
            /// the exception goes on to the error handler (500), so a constraint the command can never satisfy shows up as a bug instead of an
            /// endless "try again".
            /// </summary>
            internal static Race Answer(IRequest<Outcome<ApplicationView>> command, DbConflict conflict) => (command, conflict.Kind, conflict.Constraint) switch
            {
                // Two creates both passed the handler's checks. Which value is taken decides the answer (key first, as the handler checks).
                (CreateApplication, ConflictKind.Unique, Constraints.ApplicationKey or Constraints.ApplicationClientId) => Race.CreateTaken,
                (CreateApplication, ConflictKind.Unique, Constraints.ApplicationBrokerUser) => Race.CreateBrokerUserTaken,
                (UpdateApplication, ConflictKind.Unique, Constraints.ApplicationBrokerUser) => Race.BrokerUserTaken,
                (UpdateApplication, ConflictKind.Unique, Constraints.ApplicationClientId) => Race.ClientIdTaken,
                // The link another request just made, or the unlink another request just did: the same end state, so the same answer.
                (LinkTenant, ConflictKind.Unique, Constraints.ApplicationTenantLink) => Race.SameEndState,
                (UnlinkTenant, ConflictKind.RowGone, _) => Race.SameEndState,
                // A request for this tenant was accepted while the unlink ran.
                (UnlinkTenant, ConflictKind.ForeignKey, Constraints.RequestApplicationTenant) => Race.TenantInUse,
                // PostgreSQL aborted one of two transactions that waited on each other, or a row changed under an update: asking again answers.
                (_, ConflictKind.Aborted or ConflictKind.RowGone, _) => Race.TryAgain,
                _ => Race.Unknown,
            };

            internal enum Race { Unknown, CreateTaken, ClientIdTaken, CreateBrokerUserTaken, BrokerUserTaken, SameEndState, TenantInUse, TryAgain }

            /// <summary>
            /// What the command would have answered had it run after the transaction it raced. A link or unlink that lost answers 200 with the
            /// application as it is when read, like any read during concurrent changes: a third request that landed meanwhile shows in it.
            /// </summary>
            private async Task<IResult> AnswerAsync(Race race, string key, string? clientId, CancellationToken ct)
            {
                switch (race)
                {
                    case Race.CreateTaken or Race.CreateBrokerUserTaken when await reader.GetAsync(key, ct) is not null:
                        return Problem(StatusCodes.Status409Conflict, ErrorCodes.ApplicationExists, $"An application with key '{key}' already exists.");
                    // Which unique index PostgreSQL checked first is chance: answer in the handler's order, client id before broker user.
                    case Race.CreateBrokerUserTaken when clientId is not null && await db.Applications.AsNoTracking().AnyAsync(a => a.ClientId == clientId, ct):
                    case Race.CreateTaken:
                    case Race.ClientIdTaken:
                        return Problem(StatusCodes.Status409Conflict, ErrorCodes.ClientIdInUse, "Another application already uses this client id.");
                    case Race.CreateBrokerUserTaken:
                    case Race.BrokerUserTaken:
                        return Problem(StatusCodes.Status409Conflict, ErrorCodes.BrokerUserInUse, "Another application already publishes as this broker user.");
                    case Race.SameEndState:
                        return await reader.GetAsync(key, ct) is { } view ? Results.Ok(ToResponse(view)) : ConcurrentChange();
                    case Race.TenantInUse:
                        return Problem(StatusCodes.Status409Conflict, ErrorCodes.TenantInUse,
                            "The application has sent notifications for this tenant, so the link stays. Disable the application to stop it sending.");
                    default:
                        return ConcurrentChange();
                }
            }
        }

        private static IResult ConcurrentChange()
            => Problem(StatusCodes.Status409Conflict, ErrorCodes.ConcurrentChange, "Someone else changed this at the same time. Try again.");

        private static IResult NotFound(string message) => Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);

        private static IResult Problem(int status, string code, string detail)
            => Results.Problem(statusCode: status, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
    }
}
