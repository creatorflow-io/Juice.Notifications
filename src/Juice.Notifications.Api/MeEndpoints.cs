using Juice.MediatR;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.People;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// The signed-in person's own notifications, under <c>/api/v1/tenants/{tenantId}/me</c> (phase-2-plan I25): for a caller whose token belongs to
    /// the tenant (<see cref="ITenantAccess.IsMemberAsync"/>). The person is the subscriber of source <c>oidc</c> and the token's subject (I16),
    /// created on first use; an identity linked as an alias is the subscriber it is linked to.
    /// </summary>
    internal static class MeEndpoints
    {
        public const string OidcSource = "oidc";
        private const string MeKey = "notifications.me";

        public static void MapMeEndpoints(this IEndpointRouteBuilder app)
        {
            var me = app.MapGroup("/api/v1/tenants/{tenantId:guid}/me").RequireAuthorization().WithTags("Me");
            me.AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                var subject = http.User.FindFirst("sub")?.Value;
                if (!Guid.TryParse(http.GetRouteValue("tenantId") as string, out var tenantId) || subject is null
                    || !RequestRules.IsRecipientId(subject)
                    || !await http.RequestServices.GetRequiredService<ITenantAccess>().IsMemberAsync(http.User, tenantId, http.RequestAborted))
                {
                    return ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotTenantMember, "The caller isn't a member of this tenant.");
                }
                var db = http.RequestServices.GetRequiredService<NotificationsDbContext>();
                var clock = http.RequestServices.GetRequiredService<TimeProvider>();
                var name = http.User.FindFirst("preferred_username")?.Value ?? http.User.FindFirst("name")?.Value;
                var person = SubscriberDirectory.Fit(new ResolvedPerson(OidcSource, subject, name));
                http.Items[MeKey] = await KnownAsync(db, tenantId, person, http.RequestAborted)
                    ?? (await SubscriberDirectory.MapAsync(db, tenantId, [person], clock.GetUtcNow(), http.RequestAborted))[(OidcSource, subject)];
                return await next(context);
            });

            me.MapGet("/topics", async (Guid tenantId, HttpContext http, DirectoryReader reader, CancellationToken ct)
                => Results.Ok((await reader.TopicsForAsync(tenantId, Me(http), ct))
                    .Select(t => new MyTopicResponse(t.Topic.Key, t.Topic.Name, t.Topic.SelfService, t.Subscribed))));
            me.MapPut("/topics/{key}", (Guid tenantId, string key, HttpContext http, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new Subscribe(tenantId, key, Me(http), BySelf: true), _ => Results.NoContent(),
                    PeopleEndpoints.DirectoryConflict, ct));
            me.MapDelete("/topics/{key}", (Guid tenantId, string key, HttpContext http, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new Unsubscribe(tenantId, key, Me(http), BySelf: true), _ => Results.NoContent(),
                    PeopleEndpoints.RemoveConflict, ct));

            // Contact points (phase-2-plan M13): add, then ask for a code and enter it.
            me.MapGet("/contact-points", async (Guid tenantId, HttpContext http, ContactPointReader reader, CancellationToken ct)
                => Results.Ok((await reader.ListAsync(tenantId, Me(http), ct)).Select(ToResponse)));
            me.MapPost("/contact-points", (Guid tenantId, AddContactPointBody body, HttpContext http, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new AddContactPoint(tenantId, Me(http), body.Channel, body.Address),
                    o => Results.Created($"/api/v1/tenants/{tenantId}/me/contact-points/{o.Value!.Id}", ToResponse(o.Value)),
                    conflict => Task.FromResult<IResult?>(conflict is { Kind: ConflictKind.Unique, Constraint: Constraints.ContactPointAddress }
                        // The same address added twice at once: the second is the duplicate it would have been a moment later.
                        ? ApiResults.Problem(StatusCodes.Status409Conflict, ErrorCodes.ContactPointExists, "You already have this address.")
                        : null), ct));
            me.MapDelete("/contact-points/{id:guid}", (Guid tenantId, Guid id, HttpContext http, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new RemoveContactPoint(tenantId, Me(http), id), _ => Results.NoContent(),
                    conflict => Task.FromResult<IResult?>(conflict.Kind == ConflictKind.RowGone ? Results.NoContent() : null), ct));
            me.MapPost("/contact-points/{id:guid}/verification", async (Guid tenantId, Guid id, HttpContext http, ContactVerifier verifier, CancellationToken ct) =>
            {
                Outcome<IssuedCode> sent;
                try
                {
                    sent = await verifier.SendAsync(tenantId, Me(http), id, ct);
                }
                catch (Exception ex) when (DbConflicts.Classify(ex) is { Kind: ConflictKind.ForeignKey, Constraint: Constraints.VerificationContactPoint })
                {
                    // Removed while its code was being made: there is nothing to verify.
                    return ApiResults.NotFound("No such contact point.");
                }
                return ApiResults.Failure(sent) ?? Results.Accepted(value: new VerificationSentResponse(sent.Value!.ExpiresAt));
            });
            me.MapPost("/contact-points/{id:guid}/verify", (Guid tenantId, Guid id, VerifyContactPointBody body, HttpContext http, IMediator mediator,
                NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new VerifyContactPoint(tenantId, Me(http), id, body.Code), o => Results.Ok(ToResponse(o.Value!)),
                    conflict => Task.FromResult<IResult?>(conflict.Kind == ConflictKind.RowGone
                        // Removed, or verified by the same code at the same moment: what it is now answers either.
                        ? ApiResults.Problem(StatusCodes.Status422UnprocessableEntity, ErrorCodes.VerificationExpired, "No code is waiting for this address.")
                        : null), ct));

            // Device registrations for push (design §9.2): the app registers on start and removes its registration on sign-out.
            me.MapGet("/devices", async (Guid tenantId, HttpContext http, DeviceRegistry devices, CancellationToken ct)
                => Results.Ok((await devices.ListAsync(tenantId, Me(http), ct)).Select(ToResponse)));
            me.MapPost("/devices", async (Guid tenantId, RegisterDeviceBody body, HttpContext http, DeviceRegistry devices, CancellationToken ct) =>
            {
                var registered = await devices.RegisterAsync(tenantId, Me(http), body.Platform, body.AppId, body.Token,
                    body.Subscription is { } s ? new WebPushSubscription(s.Endpoint, s.P256dh, s.Auth) : null, body.Locale, ct);
                return ApiResults.Failure(registered) ?? Results.Ok(ToResponse(registered.Value!));
            });
            me.MapDelete("/devices/{id:guid}", async (Guid tenantId, Guid id, HttpContext http, DeviceRegistry devices, CancellationToken ct)
                => await devices.RemoveAsync(tenantId, Me(http), id, ct) ? Results.NoContent() : ApiResults.NotFound("No such device registration."));
        }

        private static ContactPointResponse ToResponse(ContactPointView v)
            => new(v.Id, v.Channel, v.Address, v.VerifiedAt is not null, v.VerifiedAt, v.VerifiedBy, v.Health, v.HealthReason, v.CreatedAt);

        private static DeviceResponse ToResponse(DeviceView v) => new(v.Id, v.Platform, v.AppId, v.Locale, v.CreatedAt, v.LastSeenAt);

        /// <summary>
        /// The person's subscriber when it is known and needs nothing written (one read): their identity's alias, or their own subscriber with the
        /// name the token gives (or none). Null sends them through the upsert, which creates them or refreshes the name.
        /// </summary>
        private static async Task<Guid?> KnownAsync(NotificationsDbContext db, Guid tenantId, ResolvedPerson person, CancellationToken ct)
        {
            var known = await db.Subscribers.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.Source == person.Source && s.ExternalId == person.ExternalId)
                .Select(s => new
                {
                    s.Id, s.MergedInto, s.Name,
                    Alias = db.SubscriberAliases.Where(a => a.TenantId == tenantId && a.Source == person.Source && a.ExternalId == person.ExternalId)
                        .Select(a => (Guid?)a.SubscriberId).FirstOrDefault(),
                })
                .SingleOrDefaultAsync(ct);
            return known switch
            {
                { Alias: { } linked } => linked,
                { } own when person.Name is null || person.Name == own.Name => own.MergedInto ?? own.Id,
                _ => null,
            };
        }

        /// <summary>The signed-in person's subscriber id, which the group's filter found.</summary>
        internal static Guid Me(HttpContext http) => (Guid)http.Items[MeKey]!;
    }
}
