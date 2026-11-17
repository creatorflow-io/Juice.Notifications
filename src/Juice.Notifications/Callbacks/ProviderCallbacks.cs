using Juice.Notifications.Data;
using Juice.Notifications.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Callbacks
{
    /// <summary>Configuration section <c>Callbacks</c> (phase-4-plan p4-d6).</summary>
    public sealed class CallbackOptions
    {
        public const string Section = "Callbacks";

        /// <summary>
        /// The service's address as providers reach it (<c>https://notifications.example.com</c>), for providers that are told where to call
        /// (Telegram's webhook, Twilio's status callback). Null when the deployment isn't reachable: those providers then send without callbacks.
        /// </summary>
        public Uri? PublicUrl { get; set; }

        /// <summary>Callbacks accepted per integration per minute; more are answered <c>429</c> (providers retry).</summary>
        public int PerMinute { get; set; } = 1200;

        /// <summary>The URL a provider calls for an integration, or null without <see cref="PublicUrl"/>.</summary>
        public Uri? UrlFor(string provider, Guid integrationId)
            => PublicUrl is null ? null : new Uri(PublicUrl, $"api/v1/callbacks/{provider}/{integrationId}");
    }

    /// <summary>What became of a callback, for the endpoint's answer.</summary>
    public enum CallbackOutcome
    {
        /// <summary>Read and applied (events seen before included): <c>200</c>.</summary>
        Accepted,
        /// <summary>No such integration, or no handler for its kind on this path: <c>404</c>.</summary>
        NotFound,
        /// <summary>The handler refused it (a bad or missing signature): <c>401</c>.</summary>
        Refused,
        /// <summary>The integration's secret isn't set: <c>503</c>, so the provider tries again once someone sets it.</summary>
        Unavailable,
    }

    public sealed record CallbackAnswer(CallbackOutcome Outcome, CallbackReply? Reply = null, int Applied = 0, int Duplicates = 0);

    /// <summary>
    /// Provider callbacks (design §9, §10.2; phase-4-plan I40): verified by the integration's provider handler, each event recorded once in
    /// <c>provider_events</c> and applied in the same transaction:
    /// <list type="bullet">
    /// <item><b>delivered</b>: the delivery is <c>Delivered</c>. Also from <c>Sending</c> or <c>Retrying</c>, and from a failed unknown outcome:
    /// the receipt shows it went out, so it isn't sent again;</item>
    /// <item><b>bounced</b> (hard): the delivery is <c>Bounced</c> (<c>Destination</c>) and a person's contact point fails;</item>
    /// <item><b>soft bounce</b>: a working contact point is marked bouncing (still used); the delivery stays as the provider retries it;</item>
    /// <item><b>complaint</b>: the contact point fails, so nothing more is sent to it until the person verifies it again;</item>
    /// <item><b>interaction</b>: recorded; applied from M27 (chat buttons).</item>
    /// </list>
    /// A delivery is found by our id, or by the provider's message id through this integration. An event for a delivery we don't have is still
    /// recorded (with no delivery), so a repeat isn't looked for again.
    /// </summary>
    public sealed class ProviderCallbacks(NotificationsDbContext db, IEnumerable<ICallbackHandler> handlers, TimeProvider clock, ILogger<ProviderCallbacks> logger)
    {
        public async Task<CallbackAnswer> ReceiveAsync(string provider, Guid integrationId, CallbackRequest request, CancellationToken ct)
        {
            var integration = await db.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.Id == integrationId, ct);
            var handler = integration is null ? null
                : handlers.FirstOrDefault(h => string.Equals(h.Provider, provider, StringComparison.Ordinal) && h.Handles(integration.Kind));
            if (integration is null || handler is null)
            {
                return new CallbackAnswer(CallbackOutcome.NotFound);
            }
            string? secret;
            try
            {
                secret = integration.SecretRef is { } reference ? SecretReferences.Resolve(reference) : null;
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError("A {Provider} callback for integration {Integration} can't be checked: {Error}", provider, integration.Key, ex.Message);
                return new CallbackAnswer(CallbackOutcome.Unavailable);
            }
            var read = await handler.ReadAsync(request, new IntegrationContext(integration.Id, integration.Key, integration.Kind, integration.Settings, secret), ct);
            if (!read.Verified)
            {
                logger.LogWarning("A {Provider} callback for integration {Integration} in tenant {Tenant} was refused: {Refusal}", provider, integration.Key,
                    integration.TenantId, read.Refusal);
                Telemetry.Callbacks.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("outcome", "refused"));
                return new CallbackAnswer(CallbackOutcome.Refused);
            }
            int applied = 0, duplicates = 0;
            foreach (var e in read.Events)
            {
                if (await ApplyAsync(provider, integration, e, ct))
                {
                    applied++;
                }
                else
                {
                    duplicates++;
                }
            }
            Telemetry.Callbacks.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("outcome", "accepted"));
            return new CallbackAnswer(CallbackOutcome.Accepted, read.Reply, applied, duplicates);
        }

        /// <summary>Records and applies one event in a transaction of its own; false when it was seen before.</summary>
        private async Task<bool> ApplyAsync(string provider, Integration integration, CallbackEvent e, CancellationToken ct)
        {
            var tenant = integration.TenantId;
            var eventId = e.EventId is { Length: > 200 } long_ ? long_[..200] : e.EventId;
            if (await db.ProviderEvents.AnyAsync(x => x.TenantId == tenant && x.Provider == provider && x.ProviderEventId == eventId, ct))
            {
                return false;
            }
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var delivery = e.DeliveryId is { } id
                ? await db.Deliveries.SingleOrDefaultAsync(d => d.TenantId == tenant && d.Id == id, ct)
                : e.ProviderMessageId is { Length: > 0 } messageId
                    ? await db.Deliveries.Where(d => d.TenantId == tenant && d.ProviderMessageId == messageId && d.IntegrationId == integration.Id)
                        .OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct)
                    : null;
            var now = clock.GetUtcNow();
            db.ProviderEvents.Add(new ProviderEvent
            {
                TenantId = tenant, Provider = provider, ProviderEventId = eventId, IntegrationId = integration.Id, DeliveryId = delivery?.Id,
                Kind = e.Kind.ToString(), At = e.At, ReceivedAt = now,
            });
            if (delivery is not null)
            {
                await ApplyAsync(delivery, e, now, ct);
            }
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (DbConflicts.Classify(ex) is { Kind: ConflictKind.Unique, Constraint: Constraints.ProviderEventOnce })
            {
                // The same event at the same moment through another request: that one applied it.
                db.ChangeTracker.Clear();
                return false;
            }
            catch (Exception ex) when (DbConflicts.Classify(ex) is { Kind: ConflictKind.RowGone or ConflictKind.Aborted })
            {
                // A dispatcher saved the delivery at the same moment: once more on what it is now (the event isn't recorded yet).
                db.ChangeTracker.Clear();
                await tx.RollbackAsync(CancellationToken.None);
                return await ApplyAsync(provider, integration, e, ct);
            }
            db.ChangeTracker.Clear();
            return true;
        }

        private async Task ApplyAsync(Delivery delivery, CallbackEvent e, DateTimeOffset now, CancellationToken ct)
        {
            var detail = e.Detail is { Length: > 500 } d ? d[..500] : e.Detail;
            switch (e.Kind)
            {
                case CallbackKind.Delivered when delivery.Status is DeliveryStatus.Accepted or DeliveryStatus.Sending or DeliveryStatus.Retrying
                    || (delivery.Status == DeliveryStatus.Failed && delivery.LastError?.StartsWith("unknown_outcome", StringComparison.Ordinal) == true):
                    End(delivery, DeliveryStatus.Delivered, null, null, e.At);
                    break;
                case CallbackKind.Bounced:
                    if (delivery.Status is DeliveryStatus.Accepted or DeliveryStatus.Delivered or DeliveryStatus.Sending or DeliveryStatus.Retrying)
                    {
                        End(delivery, DeliveryStatus.Bounced, FailureCategory.Destination, detail ?? "The provider reported a bounce.", e.At);
                    }
                    await MarkContactPointAsync(delivery, ContactHealth.Failed, detail ?? "The address bounced.", now, ct);
                    break;
                case CallbackKind.SoftBounced:
                    await MarkContactPointAsync(delivery, ContactHealth.Bouncing, detail ?? "The address bounced softly.", now, ct);
                    break;
                case CallbackKind.Complained:
                    await MarkContactPointAsync(delivery, ContactHealth.Failed, "The person complained about a message: " + (detail ?? "reported as spam."), now, ct);
                    break;
            }
        }

        /// <summary>A callback's ending: claimable no more, and the version moves on so a dispatcher holding it loses its save.</summary>
        private static void End(Delivery delivery, DeliveryStatus status, FailureCategory? category, string? error, DateTimeOffset at)
        {
            delivery.Status = status;
            delivery.FailureCategory = category;
            delivery.LastError = error;
            delivery.CompletedAt = at;
            delivery.NextAt = null;
            delivery.ClaimedBy = null;
            delivery.LeaseUntil = null;
            delivery.Version++;
        }

        /// <summary>
        /// The person's contact point the delivery went to, if it went to one: <c>Failed</c> wins over <c>Bouncing</c>, which only marks a working
        /// point. Not audited: the provider's report, not someone's change. So, as the dispatcher's own marking, an update statement (in this
        /// event's transaction), not a tracked change, which the audit would record as one.
        /// </summary>
        private async Task MarkContactPointAsync(Delivery delivery, ContactHealth health, string reason, DateTimeOffset at, CancellationToken ct)
        {
            if (!delivery.DestinationKey.StartsWith("cp:", StringComparison.Ordinal) || !Guid.TryParse(delivery.DestinationKey[3..], out var pointId))
            {
                return;
            }
            var marked = await db.ContactPoints
                .Where(c => c.TenantId == delivery.TenantId && c.Id == pointId && c.Health != ContactHealth.Failed
                    && (health == ContactHealth.Failed || c.Health == ContactHealth.Ok))
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.Health, health).SetProperty(x => x.HealthReason, reason).SetProperty(x => x.HealthChangedAt, at), ct);
            if (marked > 0 && health == ContactHealth.Failed)
            {
                logger.LogWarning("Contact point {ContactPoint} in tenant {Tenant} failed: {Reason}", pointId, delivery.TenantId, reason);
                Telemetry.ContactPointsFailed.Add(1);
            }
        }
    }
}
