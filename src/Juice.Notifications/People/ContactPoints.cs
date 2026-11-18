using System.Security.Cryptography;
using System.Text.Json;
using Juice.MediatR;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.Types;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.People
{
    // Contact points (design §9, §13.2, phase-2-plan M13): where a person can be reached on each channel. The person adds an address and proves it
    // with a code sent through the pipeline itself (I20); a source may vouch for one (I15). Only verified, working ones are ever planned (§6.4).

    /// <summary>
    /// The service's own application and types: <c>notifications.contact.verify</c> carries a verification code (phase-2-plan I20). Provisioned at
    /// start; never a producer (no token maps to it) and not changeable through the API.
    /// </summary>
    public static class ServiceTypes
    {
        public const string ApplicationKey = "notifications";
        /// <summary>A client id no token names (the producer check also refuses this application by key).</summary>
        public const string ClientId = "urn:juice-notifications:service";
        public const string VerifyType = "notifications.contact.verify";
        /// <summary>A route is storming (phase-4-plan I43): told to the tenant's notification admins, once per storm.</summary>
        public const string StormType = "notifications.storm";

        public static TypeDeclaration StormDeclaration { get; } = new(
            StormType, "Notification storm", "operations", DisplayGroups.System, "high", Mandatory: false, ExternalRecipients: false,
            JsonElement.Parse("""
                {
                  "type": "object",
                  "required": ["route", "threshold", "count"],
                  "additionalProperties": false,
                  "properties": { "route": { "type": "string" }, "threshold": { "type": "integer" }, "count": { "type": "integer" } }
                }
                """),
            CorrelationRules.None, null, null,
            JsonElement.Parse("""
                {
                  "inbox": "Title: Route {{ data.route }} is storming\n{{ data.count }} notifications reached it in a minute; the threshold is {{ data.threshold }}. Until it calms down, its targets get them as a digest each minute."
                }
                """));

        /// <summary>Whether a type is the service's own (under its application's key).</summary>
        public static bool IsServiceType(string typeKey) => typeKey.StartsWith(ApplicationKey + ".", StringComparison.Ordinal);

        /// <summary>
        /// The verification code's template per channel a person verifies an address on: email, and SMS when its package is registered (M29).
        /// Channels whose accounts are linked (Slack, Telegram) need none.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> VerifyTemplates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["email"] = "Subject: Your verification code: {{ data.code }}\n\nYour verification code is {{ data.code }}. It works for {{ data.minutes }} minutes.\n\nIf you didn't add this address to your notifications, ignore this message.",
            ["sms"] = "Your verification code is {{ data.code }}. It works for {{ data.minutes }} minutes.",
        };

        /// <summary>The verification type with a template for each registered channel in <see cref="VerifyTemplates"/>.</summary>
        public static TypeDeclaration VerifyDeclarationFor(IChannelRegistry channels)
            => VerifyDeclaration with
            {
                Templates = JsonSerializer.SerializeToElement(VerifyTemplates.Where(t => channels.Find(t.Key) is not null)
                    .ToDictionary(t => t.Key, t => t.Value)),
            };

        public static TypeDeclaration VerifyDeclaration { get; } = new(
            VerifyType, "Contact verification", "account", DisplayGroups.System, "high", Mandatory: true, ExternalRecipients: true,
            JsonElement.Parse("""
                {
                  "type": "object",
                  "required": ["code", "minutes"],
                  "additionalProperties": false,
                  "properties": { "code": { "type": "string", "pattern": "^[0-9]{6}$" }, "minutes": { "type": "integer" } }
                }
                """),
            CorrelationRules.None, ["data.code"], null,
            JsonElement.Parse("""
                {
                  "email": "Subject: Your verification code: {{ data.code }}\n\nYour verification code is {{ data.code }}. It works for {{ data.minutes }} minutes.\n\nIf you didn't add this address to your notifications, ignore this message."
                }
                """));
    }

    public sealed record ContactPointView(Guid Id, string Channel, string Address, DateTimeOffset? VerifiedAt, string? VerifiedBy, ContactHealth Health,
        string? HealthReason, DateTimeOffset CreatedAt);

    /// <summary>A person adds an address on a channel, unverified until they enter the code sent to it.</summary>
    public sealed record AddContactPoint(Guid NotificationTenantId, Guid SubscriberId, string? Channel, string? Address) : MessageBase, IRequest<Outcome<ContactPointView>>;

    public sealed record RemoveContactPoint(Guid NotificationTenantId, Guid SubscriberId, Guid ContactPointId) : MessageBase, IRequest<Outcome<bool>>;

    /// <summary>
    /// Makes a new code for a contact point, replacing any outstanding one, and returns it for sending (<see cref="ContactVerifier"/>). Refused
    /// for one that is verified and working, when the tenant can't send on its channel, and past the limits in <see cref="ContactRules"/>.
    /// </summary>
    public sealed record IssueVerificationCode(Guid NotificationTenantId, Guid SubscriberId, Guid ContactPointId) : MessageBase, IRequest<Outcome<IssuedCode>>;

    public sealed record IssuedCode(Guid ContactPointId, string Channel, string Code, DateTimeOffset SentAt, DateTimeOffset ExpiresAt);

    /// <summary>The person enters the code: right, and the contact point is verified (and working again, if it had failed).</summary>
    public sealed record VerifyContactPoint(Guid NotificationTenantId, Guid SubscriberId, Guid ContactPointId, string? Code) : MessageBase, IRequest<Outcome<ContactPointView>>;

    public static class ContactRules
    {
        public const int MaxPerPerson = 20;
        public const int MaxTries = 5;
        public const int SendsPerHour = 10;
        public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan ResendAfter = TimeSpan.FromSeconds(60);
        public const string Self = "self";

        /// <summary>Channels a person can have contact points on: registered, reaching people, with an address kind.</summary>
        public static bool TakesContactPoints(ChannelDefinition? channel)
            => channel is not null && channel.Audience.HasFlag(ChannelAudience.People) && channel.Address is not (AddressKind.None or AddressKind.Device);
    }

    internal sealed class ContactPointHandlers(NotificationsDbContext db, ContactCrypto crypto, IChannelRegistry channels, TimeProvider clock) :
        IRequestHandler<AddContactPoint, Outcome<ContactPointView>>, IRequestHandler<RemoveContactPoint, Outcome<bool>>,
        IRequestHandler<IssueVerificationCode, Outcome<IssuedCode>>, IRequestHandler<VerifyContactPoint, Outcome<ContactPointView>>
    {
        public async ValueTask<Outcome<ContactPointView>> Handle(AddContactPoint r, CancellationToken ct)
        {
            var channel = r.Channel is null ? null : channels.Find(r.Channel);
            if (!ContactRules.TakesContactPoints(channel))
            {
                var allowed = string.Join(", ", channels.All.Where(ContactRules.TakesContactPoints).Select(c => c.Key).Order(StringComparer.Ordinal));
                return Outcome<ContactPointView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/channel", $"A contact point's channel is one of: {allowed}.")]);
            }
            if (channel!.Address == AddressKind.Account)
            {
                // Never entered by hand (phase-4-plan I41, D23): the bot hears a code from the account, which proves it is the person's.
                return Outcome<ContactPointView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/channel", $"A {channel.Key} account is linked through the bot: ask for a link code (POST …/me/links) and send it to the bot.")]);
            }
            if (RequestRules.NormalizeAddress(channel!, r.Address) is not { } address)
            {
                return Outcome<ContactPointView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/address", $"The address isn't a valid {channel!.Address.ToString().ToLowerInvariant()} address.")]);
            }
            var self = await DirectoryReader.SelfAsync(db, r.NotificationTenantId, r.SubscriberId, ct);
            var hash = crypto.ContactHash(r.NotificationTenantId, channel!.Key, address);
            var mine = await db.ContactPoints.AsNoTracking().Where(c => c.TenantId == r.NotificationTenantId && self.Contains(c.SubscriberId))
                .Select(c => new { c.Id, c.Channel, c.AddressHash }).ToListAsync(ct);
            if (mine.FirstOrDefault(c => c.Channel == channel.Key && c.AddressHash == hash) is { } existing)
            {
                return Outcome<ContactPointView>.Conflict(ErrorCodes.ContactPointExists, $"You already have this address, as contact point {existing.Id}.");
            }
            if (mine.Count >= ContactRules.MaxPerPerson)
            {
                return Outcome<ContactPointView>.Conflict(ErrorCodes.ContactPointLimit, $"You have {ContactRules.MaxPerPerson} contact points; remove one first.");
            }
            var point = new ContactPoint
            {
                TenantId = r.NotificationTenantId, SubscriberId = r.SubscriberId, Channel = channel.Key, AddressHash = hash, KeyId = crypto.KeyId,
                CreatedAt = clock.UtcNow(), Address = crypto.ProtectAddress(r.NotificationTenantId, r.SubscriberId, channel.Key, address),
                Masked = ContactCrypto.Mask(address),
            };
            db.ContactPoints.Add(point);
            return Outcome<ContactPointView>.Created(ContactPointReader.View(point, address));
        }

        public async ValueTask<Outcome<bool>> Handle(RemoveContactPoint r, CancellationToken ct)
        {
            var self = await DirectoryReader.SelfAsync(db, r.NotificationTenantId, r.SubscriberId, ct);
            var point = await db.ContactPoints.SingleOrDefaultAsync(c => c.TenantId == r.NotificationTenantId && c.Id == r.ContactPointId
                && self.Contains(c.SubscriberId), ct);
            if (point is null)
            {
                return Outcome<bool>.NotFound("No such contact point.");
            }
            // Its outstanding code goes with it (cascade). Deliveries planned to it fail at sending: the address is gone.
            db.ContactPoints.Remove(point);
            return Outcome<bool>.Ok(true);
        }

        public async ValueTask<Outcome<IssuedCode>> Handle(IssueVerificationCode r, CancellationToken ct)
        {
            var tenant = r.NotificationTenantId;
            var self = await DirectoryReader.SelfAsync(db, tenant, r.SubscriberId, ct);
            var point = await db.ContactPoints.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenant && c.Id == r.ContactPointId
                && self.Contains(c.SubscriberId), ct);
            if (point is null)
            {
                return Outcome<IssuedCode>.NotFound("No such contact point.");
            }
            if (point.VerifiedAt is not null && point.Health != ContactHealth.Failed)
            {
                return Outcome<IssuedCode>.Conflict(ErrorCodes.AlreadyVerified, "This address is verified and working.");
            }
            // The code goes out as the service's own type: it needs that type's template for the channel and an enabled integration for it.
            var templated = await (
                from type in db.Types
                join app in db.Applications on type.ApplicationId equals app.Id
                join template in db.Templates on type.Id equals template.TypeId
                where app.Key == ServiceTypes.ApplicationKey && type.Key == ServiceTypes.VerifyType && template.Channel == point.Channel && template.TenantId == null
                select template.Id).AnyAsync(ct);
            if (!templated || !await db.Integrations.AnyAsync(i => i.TenantId == tenant && i.Channel == point.Channel && i.Enabled, ct))
            {
                return Outcome<IssuedCode>.Unprocessable(ErrorCodes.ChannelUnavailable, $"This tenant can't send {point.Channel} verification codes now.");
            }
            var now = clock.UtcNow();
            var outstanding = await db.ContactVerifications.AsNoTracking().SingleOrDefaultAsync(v => v.TenantId == tenant && v.ContactPointId == point.Id, ct);
            if (outstanding is not null && outstanding.SentAt > now - ContactRules.ResendAfter)
            {
                return TooSoon(outstanding.SentAt, now);
            }
            // The person's hourly allowance, taken in one statement so two requests at once can't both take the last one. It commits now (a
            // handler runs before its transaction opens); ContactVerifier gives it back if nothing is sent.
            var hourAgo = now - TimeSpan.FromHours(1);
            var counted = await db.Database.ExecuteSqlAsync($"""
                UPDATE notifications.subscribers
                SET verification_window_start = CASE WHEN verification_window_start IS NULL OR verification_window_start <= {hourAgo} THEN {now} ELSE verification_window_start END,
                    verifications_in_window = CASE WHEN verification_window_start IS NULL OR verification_window_start <= {hourAgo} THEN 1 ELSE verifications_in_window + 1 END
                WHERE tenant_id = {tenant} AND id = {r.SubscriberId}
                  AND (verification_window_start IS NULL OR verification_window_start <= {hourAgo} OR verifications_in_window < {ContactRules.SendsPerHour})
                """, ct);
            if (counted == 0)
            {
                return Outcome<IssuedCode>.TooManyRequests($"You have asked for {ContactRules.SendsPerHour} codes in the last hour; try again later.");
            }
            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var expires = now + ContactRules.CodeLifetime;
            var hash = CodeHash(tenant, point.Id, code);
            // One code at a time: a new one replaces the old, with fresh tries, unless another was made within the minute (two requests at once:
            // only one of them makes a code). The loser hands back the allowance it took, and so does a request whose point was removed meanwhile.
            int made;
            try
            {
                made = await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO notifications.contact_verifications AS v (tenant_id, contact_point_id, code_hash, sent_at, expires_at, tries)
                    VALUES ({tenant}, {point.Id}, {hash}, {now}, {expires}, 0)
                    ON CONFLICT (tenant_id, contact_point_id) DO UPDATE SET code_hash = excluded.code_hash, sent_at = excluded.sent_at,
                        expires_at = excluded.expires_at, tries = 0
                    WHERE v.sent_at <= {now - ContactRules.ResendAfter}
                    """, ct);
            }
            catch (Exception ex) when (DbConflicts.Classify(ex) is { Kind: ConflictKind.ForeignKey })
            {
                await ContactVerifier.GiveBackAllowanceAsync(db, tenant, r.SubscriberId, now);
                throw; // the API answers it: no such contact point
            }
            if (made == 0)
            {
                await ContactVerifier.GiveBackAllowanceAsync(db, tenant, r.SubscriberId, now);
                return TooSoon(now, now);
            }
            return Outcome<IssuedCode>.Ok(new IssuedCode(point.Id, point.Channel, code, now, expires));
        }

        private static Outcome<IssuedCode> TooSoon(DateTimeOffset sentAt, DateTimeOffset now)
            => Outcome<IssuedCode>.TooManyRequests(
                $"A code was sent less than a minute ago; ask again in {(int)Math.Ceiling((sentAt + ContactRules.ResendAfter - now).TotalSeconds)} seconds.");

        public async ValueTask<Outcome<ContactPointView>> Handle(VerifyContactPoint r, CancellationToken ct)
        {
            var tenant = r.NotificationTenantId;
            if (r.Code is not { Length: 6 } code || !code.All(char.IsAsciiDigit))
            {
                return Outcome<ContactPointView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.", [new FieldError("/code", "A code is six digits.")]);
            }
            var self = await DirectoryReader.SelfAsync(db, tenant, r.SubscriberId, ct);
            var point = await db.ContactPoints.SingleOrDefaultAsync(c => c.TenantId == tenant && c.Id == r.ContactPointId && self.Contains(c.SubscriberId), ct);
            if (point is null)
            {
                return Outcome<ContactPointView>.NotFound("No such contact point.");
            }
            var now = clock.UtcNow();
            // The try is counted before the code is compared, in one statement, so tries made at once can't pass the limit; and it commits now (a
            // handler runs before its transaction opens), so a wrong code counts even if nothing else is saved.
            var counted = await db.ContactVerifications.Where(v => v.TenantId == tenant && v.ContactPointId == point.Id && v.ExpiresAt > now
                && v.Tries < ContactRules.MaxTries).ExecuteUpdateAsync(v => v.SetProperty(x => x.Tries, x => x.Tries + 1), ct);
            var verification = counted == 0 ? null
                : await db.ContactVerifications.AsNoTracking().SingleOrDefaultAsync(v => v.TenantId == tenant && v.ContactPointId == point.Id, ct);
            if (verification is null)
            {
                return Outcome<ContactPointView>.Unprocessable(ErrorCodes.VerificationExpired,
                    "No code is waiting for this address: it expired, its tries ran out, or none was sent. Ask for a new one.");
            }
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(verification.CodeHash), Convert.FromHexString(CodeHash(tenant, point.Id, code))))
            {
                var left = Math.Max(0, ContactRules.MaxTries - verification.Tries);
                return Outcome<ContactPointView>.Unprocessable(ErrorCodes.VerificationFailed,
                    left > 0 ? $"The code is wrong; {left} {(left == 1 ? "try" : "tries")} left." : "The code is wrong, and that was the last try. Ask for a new one.");
            }
            // The code was used: it goes, so it can't verify the address again after the person removes and adds it.
            db.ContactVerifications.Remove(verification);
            point.VerifiedAt = now;
            point.VerifiedBy = ContactRules.Self;
            if (point.Health != ContactHealth.Ok)
            {
                // A code that arrived proves the address works again.
                point.Health = ContactHealth.Ok;
                point.HealthReason = null;
                point.HealthChangedAt = now;
            }
            // Verified even if the address can't be read back now (its key dropped since): the code reached it.
            return Outcome<ContactPointView>.Ok(ContactPointReader.View(point, crypto.TryUnprotectAddress(point) ?? ""));
        }

        private string CodeHash(Guid tenantId, Guid contactPointId, string code) => crypto.Hash("verify", tenantId.ToString(), contactPointId.ToString(), code);
    }

    /// <summary>A person's contact points, addresses decrypted (theirs and any of an identity linked into them).</summary>
    public sealed class ContactPointReader(NotificationsDbContext db, ContactCrypto crypto, ILogger<ContactPointReader> logger)
    {
        internal static ContactPointView View(ContactPoint c, string address)
            => new(c.Id, c.Channel, address, c.VerifiedAt, c.VerifiedBy, c.Health, c.HealthReason, c.CreatedAt);

        public async Task<IReadOnlyList<ContactPointView>> ListAsync(Guid tenantId, Guid subscriberId, CancellationToken ct)
        {
            var self = await DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            var points = await db.ContactPoints.AsNoTracking().Where(c => c.TenantId == tenantId && self.Contains(c.SubscriberId))
                .OrderBy(c => c.Channel).ThenBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
            var views = new List<ContactPointView>(points.Count);
            foreach (var point in points)
            {
                if (crypto.TryUnprotectAddress(point) is not { } address)
                {
                    // Its key was dropped (or the row damaged): listed, so the person can remove it and add it again.
                    logger.LogWarning("Contact point {ContactPoint} in tenant {Tenant} can't be decrypted", point.Id, tenantId);
                    address = "";
                }
                views.Add(View(point, address));
            }
            return views;
        }
    }

    /// <summary>
    /// Sends a verification code: issues it (<see cref="IssueVerificationCode"/>), then submits it as a <c>notifications.contact.verify</c> request
    /// naming the contact point itself (<see cref="RecipientKinds.ContactPoint"/>), through intake like any producer's (phase-2-plan I20), so the send
    /// shows in history and is retried like any other, and the address never appears in the request, its recipient or the delivery's history.
    /// </summary>
    public sealed class ContactVerifier(IMediator mediator, NotificationsDbContext db, Stages.StageNudges nudges, TimeProvider clock,
        ILogger<ContactVerifier> logger)
    {
        public async Task<Outcome<IssuedCode>> SendAsync(Guid tenantId, Guid subscriberId, Guid contactPointId, CancellationToken ct)
        {
            var issued = await mediator.Send(new IssueVerificationCode(tenantId, subscriberId, contactPointId), ct);
            if (issued.Value is not { } code)
            {
                return issued;
            }
            Outcome<AcceptedView> accepted;
            try
            {
                accepted = await SubmitAsync(tenantId, code, ct);
            }
            catch (Exception failure)
            {
                // Nothing was sent (the database failed, the request was cancelled): as for a refusal below. The original failure is what the
                // caller sees, even if giving back fails too (the same database outage, most likely).
                try
                {
                    await GiveBackAsync(tenantId, subscriberId, code);
                }
                catch (Exception giveBack)
                {
                    logger.LogWarning(giveBack, "Couldn't give back the verification code for contact point {ContactPoint} after {Failure}",
                        code.ContactPointId, failure.GetType().Name);
                }
                throw;
            }
            if (accepted.Value is not null)
            {
                nudges.Wake(Planning.PlannerStage.StageName); // committed: the planner takes it now, not at its next poll
                return issued;
            }
            // Nothing will be sent (the service's application disabled, its type refused): the code and the person's allowance go back, so they
            // aren't held to the resend limits for a code that never left. A code it replaced stays replaced: the person asks again.
            await GiveBackAsync(tenantId, subscriberId, code);
            return new Outcome<IssuedCode>(accepted.Kind, null, accepted.Code, accepted.Message, accepted.Errors);
        }

        private async Task<Outcome<AcceptedView>> SubmitAsync(Guid tenantId, IssuedCode code, CancellationToken ct)
        {
            var app = await db.Applications.AsNoTracking().Where(a => a.Key == ServiceTypes.ApplicationKey).Select(a => new { a.Id, a.Enabled }).SingleAsync(ct);
            // The service's application sends for every tenant: linked on first use, not by a platform admin.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.application_tenants (application_id, tenant_id, linked_at) VALUES ({app.Id}, {tenantId}, {code.SentAt})
                ON CONFLICT DO NOTHING
                """, ct);
            var submission = new NotificationSubmission(ServiceTypes.VerifyType, null,
                $"verify:{code.ContactPointId}:{code.SentAt.UtcTicks}", null, [new RecipientSpec(RecipientKinds.ContactPoint, Id: code.ContactPointId.ToString())],
                new ActorSpec("system", "contact-verification"), null, null,
                JsonSerializer.SerializeToElement(new { code = code.Code, minutes = (int)ContactRules.CodeLifetime.TotalMinutes }), null, code.ExpiresAt);
            var traceParent = System.Diagnostics.Activity.Current is { IdFormat: System.Diagnostics.ActivityIdFormat.W3C } activity ? activity.Id : null;
            return await mediator.Send(new AcceptNotification(tenantId, app.Id, ServiceTypes.ApplicationKey, submission, traceParent, app.Enabled), ct);
        }

        private async Task GiveBackAsync(Guid tenantId, Guid subscriberId, IssuedCode code)
        {
            db.ChangeTracker.Clear();
            await db.ContactVerifications.Where(v => v.TenantId == tenantId && v.ContactPointId == code.ContactPointId && v.SentAt == code.SentAt)
                .ExecuteDeleteAsync(CancellationToken.None);
            await GiveBackAllowanceAsync(db, tenantId, subscriberId, clock.UtcNow());
        }

        /// <summary>Returns one send to the person's hourly allowance, if the hour that counted it is still current.</summary>
        internal static Task GiveBackAllowanceAsync(NotificationsDbContext db, Guid tenantId, Guid subscriberId, DateTimeOffset now)
            => db.Database.ExecuteSqlAsync($"""
                UPDATE notifications.subscribers SET verifications_in_window = verifications_in_window - 1
                WHERE tenant_id = {tenantId} AND id = {subscriberId} AND verifications_in_window > 0
                  AND verification_window_start > {now - TimeSpan.FromHours(1)}
                """, CancellationToken.None);
    }

    /// <summary>
    /// At start, after migrations: checks the encryption keys, and provisions the service's own application and its types (<see cref="ServiceTypes"/>).
    /// An application already using the key <c>notifications</c> for another client stops the host: that key is the service's.
    /// </summary>
    public sealed class ServiceTypesProvisioner(IServiceScopeFactory scopes, ContactCrypto crypto, IChannelRegistry channels, TimeProvider clock,
        ILogger<ServiceTypesProvisioner> logger)
        : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            crypto.Check();
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var now = clock.UtcNow();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.applications (id, key, name, client_id, broker_user, enabled, created_at, updated_at)
                VALUES ({Guid.CreateVersion7()}, {ServiceTypes.ApplicationKey}, {"Juice Notifications"}, {ServiceTypes.ClientId}, NULL, TRUE, {now}, {now})
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            var app = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Key == ServiceTypes.ApplicationKey, cancellationToken);
            if (app is null || app.ClientId != ServiceTypes.ClientId)
            {
                throw new InvalidOperationException(app is null
                    ? $"The application '{ServiceTypes.ApplicationKey}' couldn't be provisioned: another application uses the client id {ServiceTypes.ClientId}."
                    : $"An application named '{ServiceTypes.ApplicationKey}' (client {app.ClientId}) exists; that key is reserved for the service's own types. Rename it.");
            }
            using var context = NotificationsMessageContext.Begin("provisioning");
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            Outcome<TypeVersionView> declared;
            for (var tries = 0; ; tries++)
            {
                try
                {
                    declared = await mediator.Send(new DeclareType(app.Id, app.Key, ServiceTypes.VerifyDeclarationFor(channels)), cancellationToken);
                    break;
                }
                catch (Exception ex) when (tries < 3 && DbConflicts.Classify(ex) is { Kind: ConflictKind.Unique })
                {
                    // Replicas starting together declare the same version; the loser finds the winner's the next time (the same declaration).
                    db.ChangeTracker.Clear();
                }
            }
            if (declared.Value is not { } type)
            {
                throw new InvalidOperationException($"The service's type {ServiceTypes.VerifyType} was refused: {declared.Message} "
                    + string.Join("; ", declared.Errors?.Select(e => $"{e.Pointer}: {e.Message}") ?? []));
            }
            if (type.IsNew)
            {
                logger.LogInformation("Declared the service's type {Type} v{Version}", type.Key, type.Version);
            }
            var storm = await mediator.Send(new DeclareType(app.Id, app.Key, ServiceTypes.StormDeclaration), cancellationToken);
            if (storm.Value is null)
            {
                throw new InvalidOperationException($"The service's type {ServiceTypes.StormType} was refused: {storm.Message} "
                    + string.Join("; ", storm.Errors?.Select(e => $"{e.Pointer}: {e.Message}") ?? []));
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
