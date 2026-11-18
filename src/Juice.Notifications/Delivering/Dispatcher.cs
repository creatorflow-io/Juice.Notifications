using System.Diagnostics;
using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Inbox;
using Juice.Notifications.People;
using Juice.Notifications.Planning;
using Juice.Notifications.Providers;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Delivering
{
    /// <summary>
    /// The dispatcher (design §4.1 stage 5, §10): claims a due delivery, sends it through its integration's provider, records the attempt, and
    /// applies the outcome:
    /// <list type="bullet">
    /// <item><c>Delivered</c>/<c>Accepted</c>: done;</item>
    /// <item>retryable: again after exponential backoff with jitter, until the attempts run out (then <c>Failed</c>);</item>
    /// <item>permanent: <c>Failed</c> with its category, no retry;</item>
    /// <item>failover (D8, §10.3, phase-4-plan I38): a <c>Provider</c> failure that is permanent, retryable <c>Provider</c> failures past the
    /// attempts, or an integration that is disabled or has no provider, move a delivery whose address isn't the integration's own (a contact point,
    /// an external address) to the tenant's next enabled integration for the channel, keeping its id and content, with a fresh budget there.
    /// Never for <c>Destination</c>, <c>Content</c>, <c>Policy</c> or <c>RateLimited</c>, and never after an unknown outcome: another provider
    /// can't deduplicate on the first one's key;</item>
    /// <item>unknown (§10.3): retried when that is safe (a channel whose receivers deduplicate, as webhook's do on <c>webhook-id</c>; an
    /// idempotent provider; a mandatory type once), otherwise <c>Failed {Provider}</c> with <c>unknown_outcome</c>, visible and replayable.</item>
    /// </list>
    /// The attempt is written in its own transaction right after the send: the outside world saw it, so it stays recorded even when this worker
    /// turns out to have lost its lease (§11).
    /// </summary>
    public sealed class DispatcherStage(IEnumerable<INotificationProvider> providers, IChannelRegistry channels, IOptions<DeliveryOptions> options, IServiceScopeFactory scopes,
        ILogger<DispatcherStage> logger, People.ContactCrypto crypto, Events.ProducerEvents? events = null, TimeProvider? clock = null) : IStage<Delivery>
    {
        public string Name => DeliveryStageNames.Dispatcher;

        /// <summary>
        /// The step, then <c>delivery.failed</c> for each recipient whose every destination has now failed (design §5.4, phase-3-plan I37): this
        /// delivery failed, and none of theirs went out or is still to go. Saved with the step.
        /// </summary>
        public async Task ExecuteAsync(StageContext context, Delivery row, CancellationToken ct)
        {
            var before = row.Status;
            await SendAsync(context, row, ct);
            if (events is { Enabled: true } && row.Status == DeliveryStatus.Failed && before != DeliveryStatus.Failed)
            {
                await FailedAsync(context, row, ct);
            }
        }

        private async Task FailedAsync(StageContext context, Delivery row, CancellationToken ct)
        {
            var db = context.Db;
            var recipients = await (from item in db.DeliveryItems
                                    join r in db.Recipients on new { item.TenantId, Id = item.RecipientId } equals new { r.TenantId, r.Id }
                                    join n in db.Notifications on new { r.TenantId, Id = r.NotificationId } equals new { n.TenantId, n.Id }
                                    join q in db.Requests on new { n.TenantId, Id = n.RequestId } equals new { q.TenantId, q.Id }
                                    where item.TenantId == row.TenantId && item.DeliveryId == row.Id
                                    select new { Recipient = r, Request = q }).AsNoTracking().ToListAsync(ct);
            DeliveryStatus[] ended = [DeliveryStatus.Failed, DeliveryStatus.Bounced, DeliveryStatus.Expired, DeliveryStatus.Cancelled];
            foreach (var x in recipients)
            {
                var others = await (from item in db.DeliveryItems
                                    join d in db.Deliveries on new { item.TenantId, Id = item.DeliveryId } equals new { d.TenantId, d.Id }
                                    where item.TenantId == row.TenantId && item.RecipientId == x.Recipient.Id && d.Id != row.Id
                                    select new { d.Status, d.Channel }).AsNoTracking().ToListAsync(ct);
                if (others.Any(o => !ended.Contains(o.Status)))
                {
                    continue; // another destination went out or may still
                }
                var channels = others.Select(o => o.Channel).Append(row.Channel).Distinct().Order(StringComparer.Ordinal).ToList();
                await events!.RaiseAsync(db, row.TenantId, x.Request.ApplicationId, app => new Events.NotificationEvent
                {
                    Event = Events.ProducerEventNames.DeliveryFailed, Application = app, NotificationTenantId = row.TenantId, CorrelationKey = x.Request.CorrelationKey,
                    NotificationId = x.Recipient.NotificationId, RequestId = x.Request.Id, Type = x.Request.TypeKey, RecipientId = x.Recipient.Id,
                    SubscriberId = x.Recipient.SubscriberId, NamedAs = x.Recipient.NamedAs, Channels = channels, OccurredAt = context.Now,
                }, ct);
            }
        }

        /// <summary>
        /// The step itself kept failing (not a send: those are attempts): failed, visible in history and replayable. Unless a send already
        /// succeeded and was recorded, only its result never reached the row: then the delivery is what that attempt says.
        /// </summary>
        public void GiveUp(StageContext context, Delivery row, string error)
        {
            var succeeded = context.Db.Attempts.AsNoTracking()
                .Where(a => a.TenantId == row.TenantId && a.DeliveryId == row.Id && a.Number > row.Attempts
                    && (a.Outcome == AttemptOutcome.Delivered || a.Outcome == AttemptOutcome.Accepted))
                .OrderBy(a => a.Number).FirstOrDefault();
            if (succeeded is not null)
            {
                Apply(context, row, succeeded, succeeded.Number, null, 0);
                return;
            }
            // Past every recorded attempt, so a later dead-letter retry doesn't take an old one for a fresh send.
            row.Attempts = Math.Max(row.Attempts, context.Db.Attempts.AsNoTracking().Where(a => a.TenantId == row.TenantId && a.DeliveryId == row.Id)
                .Max(a => (int?)a.Number) ?? 0);
            Finish(context, row, DeliveryStatus.Failed, null, "The dispatcher failed repeatedly on this delivery: " + error, context.Now);
        }

        private async Task SendAsync(StageContext context, Delivery row, CancellationToken ct)
        {
            var db = context.Db;
            if (row.Status is not (DeliveryStatus.Pending or DeliveryStatus.Retrying))
            {
                context.Done(row, null);
                return;
            }
            var integration = await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == row.TenantId && i.Id == row.IntegrationId, ct);
            var provider = providers.FirstOrDefault(p => p.Channel == row.Channel && p.Kind == integration.Kind);
            // First, before expiry or anything that would end the delivery without sending: an attempt is written in its own transaction right
            // after the send, so it stays when the step's save is lost (a lease that ended, a failure after the send). One the row hasn't taken
            // in yet went out, so its outcome is the delivery's, not a fresh send.
            if (await ApplyRecordedAsync(context, row, provider, ct))
            {
                return;
            }
            var requests = await (
                // The item names its request: one indexed step, not a join through recipients and notifications (which, planned while the tables
                // had no statistics, timed out in the M9 load check).
                from item in db.DeliveryItems.Where(i => i.TenantId == row.TenantId && i.DeliveryId == row.Id)
                join request in db.Requests on new { item.TenantId, Id = item.RequestId } equals new { request.TenantId, request.Id }
                orderby request.Id
                select new { request.ExpiresAt, request.AcceptedAt, request.TraceParent, request.CorrelationId }).ToListAsync(ct);
            // The earliest expiry of the requests it carries (several only for a digest), as a dead-letter retry checks it; the first request's trace.
            var expiresAt = requests.Where(r => r.ExpiresAt != null).Min(r => r.ExpiresAt);
            var origin = requests.FirstOrDefault();
            if (expiresAt is { } at && at <= context.Now)
            {
                Finish(context, row, DeliveryStatus.Expired, null, "The request expired before the delivery was sent.", context.Now);
                return;
            }
            // Resolved or cancelled while this one was held (retrying, or claimed as the resolve ran): it isn't sent now (phase-3-plan I32).
            if (requests.Select(r => r.CorrelationId).OfType<Guid>().Distinct().ToList() is { Count: > 0 } correlations
                && await db.Correlations.AsNoTracking().AnyAsync(c => c.TenantId == row.TenantId && correlations.Contains(c.Id)
                    && (c.State == CorrelationState.Resolved || c.State == CorrelationState.Cancelled), ct))
            {
                Finish(context, row, DeliveryStatus.Cancelled, null, "The correlation was resolved or cancelled before it was sent.", context.Now);
                return;
            }
            if (provider is null || !integration.Enabled)
            {
                var why = provider is null ? $"No provider sends {row.Channel} through '{integration.Kind}'." : $"The integration {integration.Key} is disabled.";
                if (await NextIntegrationAsync(db, row, integration, ct) is { } next)
                {
                    FailOver(context, row, integration, next, why, context.Now);
                    return;
                }
                Finish(context, row, DeliveryStatus.Failed, FailureCategory.Provider, why, context.Now);
                return;
            }
            var attempt = row.Attempts + 1;
            if (Address(row.DestinationSnapshot) is not { } address)
            {
                // A snapshot with no address in it is the delivery's own data: no retry will find one.
                Finish(context, row, DeliveryStatus.Failed, FailureCategory.Destination, "The delivery has no address.", context.Now);
                return;
            }
            string destination;
            string? masked = null;
            if (address.ContactPointId is { } contactPointId)
            {
                // A person's contact point, read now (phase-2-plan I19): removed, unverified or failed since planning, it isn't sent to; a
                // verification code goes to the point being verified whatever its state. A key that is gone fails the step like an unset secret
                // below, so restoring the key lets the runner's retry send it; a value that doesn't decrypt never will, and fails the delivery.
                var point = await db.ContactPoints.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == row.TenantId && c.Id == contactPointId, ct);
                if (point is null || !address.Verification && (point.VerifiedAt is null || point.Health == ContactHealth.Failed))
                {
                    Finish(context, row, DeliveryStatus.Failed, FailureCategory.Destination, point is null
                        ? "The contact point was removed before the delivery was sent."
                        : $"The contact point can't be used: {(point.VerifiedAt is null ? "it isn't verified" : "it failed: " + point.HealthReason)}.", context.Now);
                    return;
                }
                try
                {
                    destination = crypto.UnprotectAddress(point);
                    masked = point.Masked;
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
                {
                    Finish(context, row, DeliveryStatus.Failed, FailureCategory.Destination, "The contact point's address can't be decrypted: " + ex.Message, context.Now);
                    return;
                }
            }
            else if (address.DeviceId is { } deviceId)
            {
                // A person's device (phase-4-plan I42), read now: one removed since planning (signed out, swept, or its token gone) isn't sent to.
                var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == row.TenantId && d.Id == deviceId, ct);
                if (device is null)
                {
                    Finish(context, row, DeliveryStatus.Failed, FailureCategory.Destination, "The device's registration was removed before the delivery was sent.", context.Now);
                    return;
                }
                try
                {
                    destination = crypto.Unprotect(device.Token, device.KeyId, People.ContactCrypto.DeviceContext(device.TenantId, device.Platform, device.AppId));
                    masked = device.Platform + " device";
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
                {
                    Finish(context, row, DeliveryStatus.Failed, FailureCategory.Destination, "The device's token can't be decrypted: " + ex.Message, context.Now);
                    return;
                }
            }
            else
            {
                // A secret (the integration's, or an address kept as a reference) that isn't set is a deployment problem someone can fix. It isn't
                // an attempt (nothing was sent): the step fails, so the runner tries it again with its backoff, and gives the delivery up as failed
                // after its step-failure limit, with the reason.
                destination = address.IsReference ? SecretReferences.Resolve(address.Value!) : address.Value!;
            }
            var secret = integration.SecretRef is { } reference ? SecretReferences.Resolve(reference) : null;

            // The producer's trace continues here, so the provider call (a webhook's HTTP request carries it on) links back to the request.
            using var activity = Telemetry.StartStep("notifications.dispatch", origin?.TraceParent, ActivityKind.Client);
            activity?.SetTag("notifications.delivery_id", row.Id);
            activity?.SetTag("notifications.channel", row.Channel);
            activity?.SetTag("notifications.integration", integration.Key);
            activity?.SetTag("notifications.attempt", attempt);
            if (row.FirstAttemptAt is null && origin is not null)
            {
                var delay = (context.Now - origin.AcceptedAt).TotalSeconds;
                var channel = row.Channel;
                context.AfterCommit.Add(() => Telemetry.DispatchDelay.Record(delay, new KeyValuePair<string, object?>("channel", channel)));
            }
            // On the service's clock (TimeProvider), as everything else it records is.
            var time = clock ?? TimeProvider.System;
            var started = time.GetTimestamp();
            SendResult result;
            try
            {
                // Not cancelled by the worker stopping: a send cut off midway may still have gone out, and would be repeated unrecorded under the
                // same number. The providers' own timeouts bound it (SMTP about 27 s, webhook 17 s); the host waits 45 s on shutdown (Hosting).
                // The container's stop grace period has to allow that too (deploy/local: stop_grace_period).
                result = await provider.SendAsync(new RenderedDelivery(row.Id, row.TenantId, row.Channel, row.Content, destination, attempt, row.Priority, row.Mandatory),
                    new IntegrationContext(integration.Id, integration.Key, integration.Kind, integration.Settings, secret), CancellationToken.None);
            }
            catch (Exception ex)
            {
                if (masked is null)
                {
                    logger.LogError(ex, "Provider {Kind} threw sending delivery {Delivery}", integration.Kind, row.Id);
                }
                else
                {
                    // A person's address may be in the exception's message (and the messages of those it wraps): logged masked, without them.
                    logger.LogError("Provider {Kind} threw sending delivery {Delivery}: {Exception}: {Message}", integration.Kind, row.Id,
                        ex.GetType().FullName, Masking.Scrub(ex.Message, destination, masked));
                }
                result = SendResult.Unknown("The provider failed unexpectedly: " + ex.Message);
            }
            if (masked is not null && result.Error is { } said)
            {
                // A server's reply often quotes the address ('55x 5.1.1 <jane@corp.com>'): kept out of the attempt, the delivery, the contact
                // point's reason and the log, as the snapshot keeps it out of history (phase-2-plan I19).
                result = result with { Error = Masking.Scrub(said, destination, masked) };
            }
            activity?.SetTag("notifications.outcome", result.Outcome.ToString());
            if (result.Outcome is not (AttemptOutcome.Delivered or AttemptOutcome.Accepted))
            {
                activity?.SetStatus(ActivityStatusCode.Error, result.Error);
            }
            var elapsed = time.GetElapsedTime(started);
            Telemetry.ProviderLatency.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("channel", row.Channel),
                new KeyValuePair<string, object?>("outcome", result.Outcome.ToString()));
            if (result is { AddressGone: true, Outcome: AttemptOutcome.PermanentFailure } && address.ContactPointId is { } gone)
            {
                await MarkFailedAsync(row.TenantId, gone, result.Error, context.Now + elapsed);
            }
            if (result is { AddressGone: true, Outcome: AttemptOutcome.PermanentFailure } && address.DeviceId is { } goneDevice)
            {
                await ForgetDeviceAsync(row.TenantId, goneDevice, result.Error);
            }
            // Timed from when the send ended, so a slow failure (a timeout) still waits its whole backoff before the next one.
            await RecordAndApplyAsync(context, row, integration, attempt, result, (int)elapsed.TotalMilliseconds, context.Now + elapsed, provider);
        }

        /// <summary>
        /// The provider said a person's address doesn't exist (SMTP 5.1.1, 5.1.6, 5.1.10): the contact point is failed, so nothing more is planned to it until they
        /// verify it again (design §9, phase-2-plan M13). Its own statement, like the attempt: the provider's answer stands even if this worker's
        /// lease ended. Not audited: it is the service's observation, not someone's change.
        /// </summary>
        private async Task MarkFailedAsync(Guid tenantId, Guid contactPointId, string? reason, DateTimeOffset at)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var text = reason is { Length: > 500 } ? reason[..500] : reason ?? "The provider said the address doesn't exist.";
            var marked = await db.ContactPoints.Where(c => c.TenantId == tenantId && c.Id == contactPointId && c.Health != ContactHealth.Failed)
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.Health, ContactHealth.Failed).SetProperty(x => x.HealthReason, text)
                    .SetProperty(x => x.HealthChangedAt, at), CancellationToken.None);
            if (marked > 0)
            {
                logger.LogWarning("Contact point {ContactPoint} in tenant {Tenant} failed: {Reason}", contactPointId, tenantId, text);
                Telemetry.ContactPointsFailed.Add(1);
            }
        }

        /// <summary>
        /// The push service said the device's token is gone (unregistered, expired, the app removed): its registration is deleted, so it is never
        /// planned again (design §9.2, phase 4 proof point 3). The app registers anew when it next starts. Its own statement, as a contact point's.
        /// </summary>
        private async Task ForgetDeviceAsync(Guid tenantId, Guid deviceId, string? reason)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            if (await db.Devices.Where(d => d.TenantId == tenantId && d.Id == deviceId).ExecuteDeleteAsync(CancellationToken.None) > 0)
            {
                logger.LogInformation("Device registration {Device} in tenant {Tenant} was removed: {Reason}", deviceId, tenantId, reason);
                Telemetry.DevicesGone.Add(1);
            }
        }

        /// <summary>
        /// Records the attempt, then applies what the recorded attempts say. Usually that is this one; but a worker whose lease ended while it sent
        /// may have recorded one too, and if either went through, the delivery did.
        /// </summary>
        private async Task RecordAndApplyAsync(StageContext context, Delivery row, Integration integration, int attempt, SendResult result, int durationMs,
            DateTimeOffset at, INotificationProvider? provider)
        {
            await RecordAsync(context, row, integration, attempt, result, durationMs, at);
            await ApplyRecordedAsync(context, row, provider, CancellationToken.None);
        }

        /// <summary>
        /// Applies the attempts recorded after the row's count, if any: the first that went through (Delivered or Accepted), otherwise the latest.
        /// The row's count moves past all of them, so none is applied twice. False when there are none.
        /// </summary>
        private async Task<bool> ApplyRecordedAsync(StageContext context, Delivery row, INotificationProvider? provider, CancellationToken ct)
        {
            var unapplied = await context.Db.Attempts.AsNoTracking().Where(a => a.TenantId == row.TenantId && a.DeliveryId == row.Id && a.Number > row.Attempts)
                .OrderBy(a => a.Number).ToListAsync(ct);
            if (unapplied.Count == 0)
            {
                return false;
            }
            // A failure from a send that started before the latest dead-letter retry (a stale worker's, recorded after the retry) doesn't answer
            // it: numbered past, but not applied, so the retry still sends. One that went through still finishes the delivery.
            var count = unapplied[^1].Number;
            // After a failover, a failure through an earlier integration (a stale worker's, recorded late) doesn't answer the new one: numbered
            // past, not applied, so the new integration still sends. One that went through still finishes the delivery.
            if (row.Failovers > 0)
            {
                unapplied = unapplied.Where(a => a.IntegrationId == row.IntegrationId || a.Outcome is AttemptOutcome.Delivered or AttemptOutcome.Accepted).ToList();
                if (unapplied.Count == 0)
                {
                    row.Attempts = count;
                    return false;
                }
            }
            if (row.ReplayedAt is { } replayed)
            {
                var answers = unapplied.Where(a => a.Outcome is AttemptOutcome.Delivered or AttemptOutcome.Accepted
                    || a.At - TimeSpan.FromMilliseconds(a.DurationMs) >= replayed).ToList();
                if (answers.Count == 0)
                {
                    row.Attempts = count;
                    return false;
                }
                unapplied = answers;
            }
            // A send that went through wins. Else a definite refusal (a 404, a 550) and an unknown outcome are weighed by which is newer: the newer
            // refusal decides (another send would be refused too), but an older one may come from a stale worker on settings since fixed, while the
            // newer send may have gone out, so then the unknown-outcome policy decides. A retryable failure only shows that one send didn't: else
            // the latest. A stale worker's attempt is timed from its own (earlier) step, so it sorts before the current owner's.
            var refusal = unapplied.LastOrDefault(a => a.Outcome == AttemptOutcome.PermanentFailure);
            var unknown = unapplied.LastOrDefault(a => a.Outcome == AttemptOutcome.Unknown);
            var chosen = unapplied.FirstOrDefault(a => a.Outcome is AttemptOutcome.Delivered or AttemptOutcome.Accepted)
                ?? (refusal is not null && (unknown is null || refusal.At >= unknown.At) ? refusal : unknown)
                ?? unapplied[^1];
            row.FirstAttemptAt ??= unapplied[0].At;
            // The budget, the backoff step and the mandatory types' one repeat after an unknown outcome count the attempts since the latest
            // dead-letter retry, and only sends that began after it: a stale worker's send recorded after the retry is numbered past, not counted.
            var budgetFrom = BudgetFrom(row);
            var sinceReplay = (await context.Db.Attempts.AsNoTracking().Where(a => a.TenantId == row.TenantId && a.DeliveryId == row.Id
                && a.Number > budgetFrom).Select(a => new { a.At, a.DurationMs, a.Outcome }).ToListAsync(CancellationToken.None))
                .Where(a => row.ReplayedAt is not { } replayed || a.At - TimeSpan.FromMilliseconds(a.DurationMs) >= replayed).ToList();
            if (FailsOver(row, chosen, sinceReplay.Count))
            {
                var current = await context.Db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == row.TenantId && i.Id == row.IntegrationId, CancellationToken.None);
                if (await NextIntegrationAsync(context.Db, row, current, CancellationToken.None) is { } next)
                {
                    row.Attempts = Math.Max(row.Attempts, count);
                    row.FirstAttemptAt ??= unapplied[0].At;
                    row.ProviderMessageId = null;
                    FailOver(context, row, current, next, chosen.Error, chosen.At);
                    return true;
                }
            }
            Apply(context, row, chosen, count, provider, sinceReplay.Count(a => a.Outcome == AttemptOutcome.Unknown), sinceReplay.Count);
            return true;
        }

        /// <summary>
        /// Writes the attempt in its own transaction, not cancelled by the worker stopping: the outside world saw the send. Numbers are unique per
        /// delivery; a worker whose lease ended while it sent can find its number taken by the new owner, and records under the next one.
        /// </summary>
        private async Task<int> RecordAsync(StageContext context, Delivery row, Integration integration, int attempt, SendResult result, int durationMs,
            DateTimeOffset at)
        {
            for (var tries = 0; ; tries++)
            {
                await using var scope = scopes.CreateAsyncScope();
                var attempts = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                attempts.Attempts.Add(new DeliveryAttempt
                {
                    TenantId = row.TenantId, DeliveryId = row.Id, Number = attempt, IntegrationId = integration.Id, At = at, Outcome = result.Outcome,
                    Category = result.Category, StatusCode = result.StatusCode, Error = Truncate(result.Error), DurationMs = durationMs,
                    ProviderMessageId = result.ProviderMessageId is { Length: > 500 } id ? id[..500] : result.ProviderMessageId,
                });
                try
                {
                    await attempts.SaveChangesAsync(CancellationToken.None);
                    Telemetry.Attempts.Add(1, new KeyValuePair<string, object?>("outcome", result.Outcome.ToString()),
                        new KeyValuePair<string, object?>("category", result.Category?.ToString() ?? "none"));
                    if (result.Outcome == AttemptOutcome.Unknown)
                    {
                        Telemetry.UnknownOutcomes.Add(1, new KeyValuePair<string, object?>("channel", row.Channel));
                    }
                    return attempt;
                }
                catch (Exception ex) when (tries < 5 && DbConflicts.Classify(ex) is { Kind: ConflictKind.Unique, Constraint: Constraints.AttemptNumber })
                {
                    attempt = 1 + (await attempts.Attempts.AsNoTracking().Where(a => a.TenantId == row.TenantId && a.DeliveryId == row.Id)
                        .MaxAsync(a => (int?)a.Number, CancellationToken.None) ?? 0);
                }
            }
        }

        /// <summary>
        /// Applies a recorded attempt's outcome to the row (§10.2), timed from when the attempt happened, not from when it is applied.
        /// <paramref name="count"/> is the attempts made so far, which can pass the chosen attempt's number.
        /// </summary>
        /// <paramref name="unknowns"/> is how many attempts since the latest dead-letter retry had an unknown outcome, this one included.
        /// <paramref name="made"/> is how many count against the budget (sends begun since the latest retry); by default all numbered since it.
        private void Apply(StageContext context, Delivery row, DeliveryAttempt chosen, int count, INotificationProvider? provider, int unknowns, int? made = null)
        {
            var error = Truncate(chosen.Error);
            var at = chosen.At;
            row.Attempts = Math.Max(row.Attempts, count);
            row.FirstAttemptAt ??= at;
            row.ProviderMessageId = chosen.ProviderMessageId ?? row.ProviderMessageId;
            var max = Math.Max(1, options.Value.MaxAttempts);
            // Counted from the latest dead-letter retry or failover, each of which gives a fresh budget; attempt numbers go on from the total.
            var attempts = made ?? row.Attempts - BudgetFrom(row);
            switch (chosen.Outcome)
            {
                case AttemptOutcome.Delivered:
                    Finish(context, row, DeliveryStatus.Delivered, null, null, at);
                    break;
                case AttemptOutcome.Accepted:
                    Finish(context, row, DeliveryStatus.Accepted, null, null, at);
                    break;
                case AttemptOutcome.RetryableFailure when attempts < max:
                    Retry(context, row, chosen.Category, error, attempts, at);
                    break;
                case AttemptOutcome.RetryableFailure:
                    Finish(context, row, DeliveryStatus.Failed, chosen.Category, $"{error} (gave up after {attempts} attempts{(row.Replays > 0 ? " since the last retry" : "")})", at);
                    break;
                case AttemptOutcome.Unknown when attempts < max && SafeToRepeat(channels.Find(row.Channel), row, provider, unknowns):
                    Retry(context, row, FailureCategory.Provider, error, attempts, at);
                    break;
                case AttemptOutcome.Unknown:
                    Finish(context, row, DeliveryStatus.Failed, FailureCategory.Provider, "unknown_outcome: " + error, at);
                    break;
                default:
                    Finish(context, row, DeliveryStatus.Failed, chosen.Category, error, at);
                    break;
            }
        }

        /// <summary>The attempts before the latest dead-letter retry or failover: the current budget counts those after.</summary>
        private static int BudgetFrom(Delivery row) => Math.Max(row.AttemptsAtReplay, row.AttemptsAtFailover);

        /// <summary>
        /// Whether this outcome moves the delivery to another integration (§10.3): a <c>Provider</c> failure that is permanent, or retryable ones
        /// past the attempts. A <c>RateLimited</c> one waits on the same integration; an unknown outcome never fails over.
        /// </summary>
        private bool FailsOver(Delivery row, DeliveryAttempt chosen, int made)
            => chosen.Category == FailureCategory.Provider && CanFailOver(row.DestinationKey)
            && (chosen.Outcome == AttemptOutcome.PermanentFailure
                || (chosen.Outcome == AttemptOutcome.RetryableFailure && made >= Math.Max(1, options.Value.MaxAttempts)));

        /// <summary>
        /// A contact point or an external address is the same whichever integration sends to it. A shared target (a webhook URL, a Slack
        /// channel) belongs to its integration, and a person's inbox has only the built-in one.
        /// </summary>
        public static bool CanFailOver(string destinationKey)
            => destinationKey.StartsWith("cp:", StringComparison.Ordinal) || destinationKey.StartsWith("ext:", StringComparison.Ordinal);

        /// <summary>
        /// The tenant's next enabled integration for the delivery's channel after <paramref name="current"/>, by priority then key (the order the
        /// planner chose the first by), so failover never comes back round; null when there is none or the destination can't fail over.
        /// </summary>
        private static Task<Integration?> NextIntegrationAsync(NotificationsDbContext db, Delivery row, Integration current, CancellationToken ct)
            => !CanFailOver(row.DestinationKey) ? Task.FromResult<Integration?>(null)
            : db.Integrations.AsNoTracking()
                .Where(i => i.TenantId == row.TenantId && i.Channel == row.Channel && i.Enabled && i.Id != current.Id
                    && (i.Priority > current.Priority || (i.Priority == current.Priority && string.Compare(i.Key, current.Key) > 0)))
                .OrderBy(i => i.Priority).ThenBy(i => i.Key).FirstOrDefaultAsync(ct);

        /// <summary>Moves the delivery to <paramref name="next"/>: same id, same content, due now, its attempts there counted afresh.</summary>
        private void FailOver(StageContext context, Delivery row, Integration from, Integration next, string? error, DateTimeOffset at)
        {
            row.IntegrationId = next.Id;
            row.Failovers++;
            row.AttemptsAtFailover = row.Attempts;
            row.Status = DeliveryStatus.Retrying;
            row.FailureCategory = FailureCategory.Provider;
            row.LastError = Truncate($"Failed over from {from.Key} to {next.Key}: {error}");
            context.Done(row, at);
            var (id, channel) = (row.Id, row.Channel);
            context.AfterCommit.Add(() =>
            {
                Telemetry.Failovers.Add(1, new KeyValuePair<string, object?>("channel", channel));
                logger.LogInformation("Delivery {Delivery} failed over from {From} to {To}: {Error}", id, from.Key, next.Key, error);
            });
        }

        /// <summary>Repeating a send whose outcome is unknown (§10.3).</summary>
        private static bool SafeToRepeat(ChannelDefinition? channel, Delivery row, INotificationProvider? provider, int unknowns)
            => channel?.RepeatsSafely == true          // receivers deduplicate (webhook-id), the same on every attempt
            || provider?.Idempotent == true             // the delivery id is the provider's idempotency key
            || (row.Mandatory && unknowns <= 1);       // a duplicate is better than silence, once

        private void Retry(StageContext context, Delivery row, FailureCategory? category, string? error, int attempt, DateTimeOffset at)
        {
            row.Status = DeliveryStatus.Retrying;
            row.FailureCategory = category;
            row.LastError = error;
            context.Done(row, at + Backoff(attempt));
            context.Wakes.Remove(Name);
        }

        private static void Finish(StageContext context, Delivery row, DeliveryStatus status, FailureCategory? category, string? error, DateTimeOffset at)
        {
            row.Status = status;
            row.FailureCategory = category;
            row.LastError = Truncate(error);
            row.CompletedAt = at;
            context.Done(row, null);
            var channel = row.Channel;
            context.AfterCommit.Add(() => Telemetry.Deliveries.Add(1, new KeyValuePair<string, object?>("channel", channel),
                new KeyValuePair<string, object?>("status", status.ToString())));
        }

        /// <summary>Exponential from <see cref="DeliveryOptions.RetryBase"/>, capped at <see cref="DeliveryOptions.RetryMax"/>, with ±20 % jitter.</summary>
        private TimeSpan Backoff(int attempt)
        {
            var o = options.Value;
            var delay = Math.Min(o.RetryBase.TotalSeconds * Math.Pow(2, Math.Min(attempt - 1, 20)), o.RetryMax.TotalSeconds);
            return TimeSpan.FromSeconds(delay * (0.8 + Random.Shared.NextDouble() * 0.4));
        }

        private sealed record SnapshotAddress(string? Value, bool IsReference, Guid? ContactPointId = null, bool Verification = false, Guid? DeviceId = null);

        /// <summary>
        /// The address in a delivery's snapshot, as stored, as a secret reference to read at send time, or as a person's contact point to read and
        /// decrypt then; null when it has none.
        /// </summary>
        private static SnapshotAddress? Address(string snapshot)
        {
            try
            {
                using var json = JsonDocument.Parse(snapshot);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                if (root.TryGetProperty("contactPoint", out var point) && point.ValueKind == JsonValueKind.String && point.TryGetGuid(out var pointId))
                {
                    return new SnapshotAddress(null, false, pointId,
                        root.TryGetProperty("verification", out var verification) && verification.ValueKind == JsonValueKind.True);
                }
                if (root.TryGetProperty("device", out var device) && device.ValueKind == JsonValueKind.String && device.TryGetGuid(out var deviceId))
                {
                    return new SnapshotAddress(null, false, DeviceId: deviceId); // read and decrypted when sending
                }
                if (root.TryGetProperty("subscriber", out var subscriber) && subscriber.ValueKind == JsonValueKind.String && subscriber.TryGetGuid(out var subscriberId))
                {
                    return new SnapshotAddress(subscriberId.ToString(), false); // a person's inbox: the provider writes their item
                }
                if (root.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.String && address.GetString() is { Length: > 0 } value)
                {
                    return new SnapshotAddress(value, false);
                }
                if (root.TryGetProperty("addressRef", out var reference) && reference.ValueKind == JsonValueKind.String && reference.GetString() is { Length: > 0 } name)
                {
                    return new SnapshotAddress(name, true);
                }
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Truncate(string? error) => error is { Length: > 2000 } ? error[..2000] : error;
    }

    public static class DispatchRegistration
    {
        public static readonly TimeSpan MinLease = TimeSpan.FromSeconds(60);

        /// <summary>The dispatcher, the phase 1 providers (webhook, SMTP) and the <c>Delivery</c> options.</summary>
        public static IServiceCollection AddDispatching(this IServiceCollection services, StageOptions options, bool runWorkers = true)
        {
            services.AddBuiltInChannels();
            services.AddContactEncryption();
            services.AddSingleton<WebhookProvider>();
            services.AddSingleton<INotificationProvider>(sp => sp.GetRequiredService<WebhookProvider>());
            services.AddSingleton<INotificationProvider, SmtpProvider>();
            services.AddInboxProvider();
            // The lease has to outlast a send, or another worker claims the row mid-send (§11): SMTP takes up to connect + 2 × request + QUIT, about
            // 27 s with the defaults; 60 s leaves room for recording and saving. A host passing a shorter lease gets this floor.
            // A copy: the caller's options (perhaps shared with other stages) stay as they were.
            var dispatcher = new StageOptions
            {
                Batch = options.Batch, Lease = options.Lease < MinLease ? MinLease : options.Lease, Poll = options.Poll, Workers = options.Workers,
                MaxStepFailures = options.MaxStepFailures, FailureBackoff = options.FailureBackoff, FailureBackoffMax = options.FailureBackoffMax,
            };
            return services.AddStage<Delivery, DispatcherStage>(dispatcher, runWorkers);
        }
    }
}
