using System.Diagnostics;
using System.Text.Json;
using Juice.Notifications.Data;
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
    /// <item>permanent: <c>Failed</c> with its category, no retry (no failover in phase 1);</item>
    /// <item>unknown (§10.3): retried when that is safe (webhook receivers deduplicate on <c>webhook-id</c>; an idempotent provider; a mandatory
    /// type once), otherwise <c>Failed {Provider}</c> with <c>unknown_outcome</c>, visible and replayable.</item>
    /// </list>
    /// The attempt is written in its own transaction right after the send: the outside world saw it, so it stays recorded even when this worker
    /// turns out to have lost its lease (§11).
    /// </summary>
    public sealed class DispatcherStage(IEnumerable<INotificationProvider> providers, IOptions<DeliveryOptions> options, IServiceScopeFactory scopes,
        ILogger<DispatcherStage> logger) : IStage<Delivery>
    {
        public string Name => DeliveryStageNames.Dispatcher;

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
                Apply(context, row, succeeded, succeeded.Number, null);
                return;
            }
            Finish(context, row, DeliveryStatus.Failed, null, "The dispatcher failed repeatedly on this delivery: " + error, context.Now);
        }

        public async Task ExecuteAsync(StageContext context, Delivery row, CancellationToken ct)
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
            var expires = await (
                from item in db.DeliveryItems.Where(i => i.TenantId == row.TenantId && i.DeliveryId == row.Id)
                join recipient in db.Recipients on new { item.TenantId, Id = item.RecipientId } equals new { recipient.TenantId, recipient.Id }
                join notification in db.Notifications on new { recipient.TenantId, Id = recipient.NotificationId } equals new { notification.TenantId, notification.Id }
                join request in db.Requests on new { notification.TenantId, Id = notification.RequestId } equals new { request.TenantId, request.Id }
                select request.ExpiresAt).FirstOrDefaultAsync(ct);
            if (expires is { } at && at <= context.Now)
            {
                Finish(context, row, DeliveryStatus.Expired, null, "The request expired before the delivery was sent.", context.Now);
                return;
            }
            if (provider is null || !integration.Enabled)
            {
                Finish(context, row, DeliveryStatus.Failed, FailureCategory.Provider,
                    provider is null ? $"No provider sends {row.Channel} through '{integration.Kind}'." : $"The integration {integration.Key} is disabled.",
                    context.Now);
                return;
            }
            var attempt = row.Attempts + 1;
            string destination;
            string? secret;
            try
            {
                destination = Destination(row.DestinationSnapshot);
                secret = integration.SecretRef is { } reference ? SecretReferences.Resolve(reference) : null;
            }
            catch (InvalidOperationException ex)
            {
                // A secret that isn't set yet is a deployment problem someone can fix: try again later rather than give up.
                await RecordAndApplyAsync(context, row, integration, attempt, SendResult.Retry(FailureCategory.Provider, ex.Message), 0);
                return;
            }

            var watch = Stopwatch.StartNew();
            SendResult result;
            try
            {
                result = await provider.SendAsync(new RenderedDelivery(row.Id, row.TenantId, row.Channel, row.Content, destination, attempt, row.Priority, row.Mandatory),
                    new IntegrationContext(integration.Id, integration.Key, integration.Kind, integration.Settings, secret), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Provider {Kind} threw sending delivery {Delivery}", integration.Kind, row.Id);
                result = SendResult.Unknown("The provider failed unexpectedly: " + ex.Message);
            }
            await RecordAndApplyAsync(context, row, integration, attempt, result, (int)watch.ElapsedMilliseconds, provider);
        }

        /// <summary>
        /// Records the attempt, then applies what the recorded attempts say. Usually that is this one; but a worker whose lease ended while it sent
        /// may have recorded one too, and if either went through, the delivery did.
        /// </summary>
        private async Task RecordAndApplyAsync(StageContext context, Delivery row, Integration integration, int attempt, SendResult result, int durationMs,
            INotificationProvider? provider = null)
        {
            await RecordAsync(context, row, integration, attempt, result, durationMs);
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
            var chosen = unapplied.FirstOrDefault(a => a.Outcome is AttemptOutcome.Delivered or AttemptOutcome.Accepted) ?? unapplied[^1];
            row.FirstAttemptAt ??= unapplied[0].At;
            Apply(context, row, chosen, unapplied[^1].Number, provider);
            return true;
        }

        /// <summary>
        /// Writes the attempt in its own transaction, not cancelled by the worker stopping: the outside world saw the send. Numbers are unique per
        /// delivery; a worker whose lease ended while it sent can find its number taken by the new owner, and records under the next one.
        /// </summary>
        private async Task<int> RecordAsync(StageContext context, Delivery row, Integration integration, int attempt, SendResult result, int durationMs)
        {
            for (var tries = 0; ; tries++)
            {
                await using var scope = scopes.CreateAsyncScope();
                var attempts = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                attempts.Attempts.Add(new DeliveryAttempt
                {
                    TenantId = row.TenantId, DeliveryId = row.Id, Number = attempt, IntegrationId = integration.Id, At = context.Now, Outcome = result.Outcome,
                    Category = result.Category, StatusCode = result.StatusCode, Error = Truncate(result.Error), DurationMs = durationMs,
                    ProviderMessageId = result.ProviderMessageId is { Length: > 500 } id ? id[..500] : result.ProviderMessageId,
                });
                try
                {
                    await attempts.SaveChangesAsync(CancellationToken.None);
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
        private void Apply(StageContext context, Delivery row, DeliveryAttempt chosen, int count, INotificationProvider? provider)
        {
            var error = Truncate(chosen.Error);
            var at = chosen.At;
            row.Attempts = Math.Max(row.Attempts, count);
            row.FirstAttemptAt ??= at;
            row.ProviderMessageId = chosen.ProviderMessageId ?? row.ProviderMessageId;
            var max = Math.Max(1, options.Value.MaxAttempts);
            var attempts = row.Attempts;
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
                    Finish(context, row, DeliveryStatus.Failed, chosen.Category, $"{error} (gave up after {attempts} attempts)", at);
                    break;
                case AttemptOutcome.Unknown when attempts < max && SafeToRepeat(row, provider, attempts):
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

        /// <summary>Repeating a send whose outcome is unknown (§10.3).</summary>
        private static bool SafeToRepeat(Delivery row, INotificationProvider? provider, int attempt)
            => row.Channel == Channels.Webhook          // receivers deduplicate on webhook-id, the same on every attempt
            || provider?.Idempotent == true             // the delivery id is the provider's idempotency key
            || (row.Mandatory && attempt == 1);         // a duplicate is better than silence, once

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
        }

        /// <summary>Exponential from <see cref="DeliveryOptions.RetryBase"/>, capped at <see cref="DeliveryOptions.RetryMax"/>, with ±20 % jitter.</summary>
        private TimeSpan Backoff(int attempt)
        {
            var o = options.Value;
            var delay = Math.Min(o.RetryBase.TotalSeconds * Math.Pow(2, Math.Min(attempt - 1, 20)), o.RetryMax.TotalSeconds);
            return TimeSpan.FromSeconds(delay * (0.8 + Random.Shared.NextDouble() * 0.4));
        }

        /// <summary>The address in a delivery's snapshot: as stored, or read now from its secret reference.</summary>
        private static string Destination(string snapshot)
        {
            using var json = JsonDocument.Parse(snapshot);
            var root = json.RootElement;
            if (root.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.String)
            {
                return address.GetString()!;
            }
            if (root.TryGetProperty("addressRef", out var reference) && reference.ValueKind == JsonValueKind.String)
            {
                return SecretReferences.Resolve(reference.GetString()!);
            }
            throw new InvalidOperationException("The delivery has no address.");
        }

        private static string? Truncate(string? error) => error is { Length: > 2000 } ? error[..2000] : error;
    }

    public static class DispatchRegistration
    {
        /// <summary>The dispatcher, the phase 1 providers (webhook, SMTP) and the <c>Delivery</c> options.</summary>
        public static IServiceCollection AddDispatching(this IServiceCollection services, StageOptions options, bool runWorkers = true)
        {
            services.AddSingleton<WebhookProvider>();
            services.AddSingleton<INotificationProvider>(sp => sp.GetRequiredService<WebhookProvider>());
            services.AddSingleton<INotificationProvider, SmtpProvider>();
            return services.AddStage<Delivery, DispatcherStage>(options, runWorkers);
        }
    }
}
