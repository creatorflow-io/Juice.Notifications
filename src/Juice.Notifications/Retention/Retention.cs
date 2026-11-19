using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Retention
{
    /// <summary>Configuration section <c>Retention</c> (design §13.2, phase-4-plan I46): the deployment's defaults; a tenant's settings override them.</summary>
    public sealed class RetentionOptions
    {
        public const string Section = "Retention";

        /// <summary>Days requests, deliveries and attempts are kept.</summary>
        public int RequestDays { get; set; } = 90;

        /// <summary>Days a delivery keeps its rendered content (a dead-letter retry needs it); then it is cleared.</summary>
        public int ContentDays { get; set; } = 14;

        /// <summary>How often the purge runs.</summary>
        public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromDays(1);

        /// <summary>How often finished requests are looked for to redact.</summary>
        public TimeSpan RedactInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>Rows per statement: deletes and updates go in batches, so a purge never holds a large lock.</summary>
        public int Batch { get; set; } = 1000;
    }

    public static class Redaction
    {
        public const string Mask = "***";

        /// <summary>
        /// Masks the paths (<c>data.a.b</c>) in a request's data, and returns the values it masked (the text of strings and numbers), so they can
        /// be masked where they were copied.
        /// </summary>
        public static (string Data, IReadOnlyList<string> Values) Apply(string data, IEnumerable<string> paths)
        {
            var root = JsonNode.Parse(data);
            var values = new List<string>();
            if (root is not JsonObject)
            {
                return (data, values);
            }
            foreach (var path in paths)
            {
                var names = path.Split('.');
                if (names.Length < 2 || names[0] != "data")
                {
                    continue;
                }
                JsonNode? node = root;
                for (var i = 1; i < names.Length - 1 && node is JsonObject; i++)
                {
                    node = node[names[i]];
                }
                if (node is JsonObject parent && parent.ContainsKey(names[^1]) && parent[names[^1]] is { } value)
                {
                    if (value is JsonValue v && (v.TryGetValue<string>(out var s) || v.GetValueKind() == JsonValueKind.Number && (s = v.ToJsonString()) is not null))
                    {
                        if (s != Mask && s.Length > 0)
                        {
                            values.Add(s);
                        }
                    }
                    parent[names[^1]] = Mask;
                }
            }
            return (root.ToJsonString(), values);
        }

        /// <summary>The text with each value (at least 3 characters: shorter ones would mask ordinary words) replaced by the mask.</summary>
        public static string Scrub(string text, IReadOnlyList<string> values)
            => values.Where(v => v.Length >= 3).OrderByDescending(v => v.Length).Aggregate(text, (t, v) => t.Replace(v, Mask, StringComparison.Ordinal));
    }

    /// <summary>
    /// Redaction and retention (design §13.2, phase-4-plan I46).
    /// <list type="bullet">
    /// <item><b>Redaction:</b> once a request is finished (its notification planned or ended, no recipient waiting or due, no escalation
    /// running), the type's <c>redact</c> paths are masked in its stored data, and their values in the inbox items made from it. Deliveries keep
    /// their rendered content (a retry sends it) until the content's retention clears it.</item>
    /// <item><b>Retention:</b> deliveries finished longer ago than the tenant's request retention go (their attempts, items and inbox items with
    /// them), then requests (their notifications and recipients with them) once none of their deliveries is left unfinished; closed digests and
    /// ended storms of that age, provider events, and used or expired link codes; and the rendered content of deliveries older than the content
    /// retention is cleared.</item>
    /// </list>
    /// </summary>
    public sealed class RetentionWorker(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, TimeProvider clock, ILogger<RetentionWorker> logger)
        : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var lastPurge = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RedactAsync(stoppingToken);
                    if (clock.GetUtcNow() - lastPurge >= options.Value.PurgeInterval)
                    {
                        await PurgeAsync(stoppingToken);
                        lastPurge = clock.GetUtcNow();
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Redaction or retention failed; it runs again later.");
                }
                try
                {
                    await Task.Delay(options.Value.RedactInterval, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Redacts finished requests, a batch at a time, until none is left; the number redacted.</summary>
        public async Task<int> RedactAsync(CancellationToken ct)
        {
            var total = 0;
            while (true)
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var now = clock.GetUtcNow();
                var redacting = await db.Types.AsNoTracking().Where(t => t.Redact.Length > 0).Select(t => t.Id).ToListAsync(ct);
                if (redacting.Count == 0)
                {
                    return total;
                }
                var ready = await (from r in db.Requests
                                   join t in db.Types on r.TypeId equals t.Id
                                   where r.RedactedAt == null && redacting.Contains(r.TypeId)
                                         && !db.Notifications.Any(n => n.TenantId == r.TenantId && n.RequestId == r.Id
                                             && (n.State == NotificationState.Planning || n.State == NotificationState.Running
                                                 || db.Recipients.Any(x => x.TenantId == n.TenantId && x.NotificationId == n.Id
                                                     && (x.Outcome == RecipientOutcome.Pending || x.NextAt != null))
                                                 || db.Escalations.Any(e => e.TenantId == n.TenantId && e.NotificationId == n.Id && e.State == EscalationState.Running)))
                                         // And every delivery of it settled: an inbox item is written when its delivery is sent, after planning.
                                         && !db.DeliveryItems.Any(i => i.TenantId == r.TenantId && i.RequestId == r.Id
                                             && db.Deliveries.Any(d => d.TenantId == i.TenantId && d.Id == i.DeliveryId
                                                 && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Sending || d.Status == DeliveryStatus.Retrying)))
                                   orderby r.AcceptedAt
                                   select new { r.TenantId, r.Id, r.Data, t.Redact }).Take(options.Value.Batch).ToListAsync(ct);
                if (ready.Count == 0)
                {
                    return total;
                }
                var redacted = 0;
                foreach (var request in ready)
                {
                    var (data, values) = Redaction.Apply(request.Data, request.Redact);
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    // The request locked, then its deliveries looked at again: a retry or replay holds the request (shared) until it commits, so
                    // one that put a delivery back after the read above is seen pending here, and one after this commits sees it redacted
                    // (History.DeliveryRules.Redacted) and leaves its inbox delivery failed.
                    await db.Database.SqlQuery<Guid>($"""
                        SELECT id AS "Value" FROM notifications.requests WHERE tenant_id = {request.TenantId} AND id = {request.Id} FOR UPDATE
                        """).ToListAsync(ct);
                    if (await db.DeliveryItems.AnyAsync(i => i.TenantId == request.TenantId && i.RequestId == request.Id
                            && db.Deliveries.Any(d => d.TenantId == i.TenantId && d.Id == i.DeliveryId
                                && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Sending || d.Status == DeliveryStatus.Retrying)), ct))
                    {
                        await transaction.RollbackAsync(ct);
                        continue; // put back meanwhile: redacted once it settles again
                    }
                    redacted++;
                    await db.Requests.Where(r => r.TenantId == request.TenantId && r.Id == request.Id && r.RedactedAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.Data, data).SetProperty(r => r.RedactedAt, now), ct);
                    if (values.Count > 0)
                    {
                        var items = await db.InboxItems.Where(i => i.TenantId == request.TenantId
                                && db.DeliveryItems.Any(d => d.TenantId == request.TenantId && d.RequestId == request.Id && d.DeliveryId == i.DeliveryId))
                            .Select(i => new { i.Id, i.Title, i.Body }).ToListAsync(ct);
                        foreach (var item in items)
                        {
                            var (title, body) = (Redaction.Scrub(item.Title, values), Redaction.Scrub(item.Body, values));
                            if (title != item.Title || body != item.Body)
                            {
                                await db.InboxItems.Where(i => i.TenantId == request.TenantId && i.Id == item.Id)
                                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.Title, title).SetProperty(i => i.Body, body), ct);
                            }
                        }
                    }
                    await transaction.CommitAsync(ct);
                }
                total += redacted;
                Telemetry.Redacted.Add(redacted);
                if (redacted == 0)
                {
                    return total; // every one of the batch was put back meanwhile: the next pass takes them
                }
            }
        }

        /// <summary>One purge pass over every tenant; how many deliveries and requests it removed and contents it cleared.</summary>
        public async Task<(int Deliveries, int Requests, int Contents)> PurgeAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var now = clock.GetUtcNow();
            var batch = options.Value.Batch;
            var settings = await db.TenantSettings.AsNoTracking().ToDictionaryAsync(s => s.TenantId, ct);
            var tenants = await db.Requests.AsNoTracking().Select(r => r.TenantId).Distinct().ToListAsync(ct);
            int deliveries = 0, requests = 0, contents = 0;
            foreach (var tenant in tenants)
            {
                var own = settings.GetValueOrDefault(tenant);
                var keep = now.AddDays(-(own?.RequestRetentionDays ?? options.Value.RequestDays));
                var content = now.AddDays(-(own?.ContentRetentionDays ?? options.Value.ContentDays));
                var finished = new[] { DeliveryStatus.Delivered, DeliveryStatus.Accepted, DeliveryStatus.Failed, DeliveryStatus.Bounced, DeliveryStatus.Expired,
                    DeliveryStatus.Cancelled };
                deliveries += await InBatchesAsync(() => db.Deliveries.Where(d => d.TenantId == tenant && d.CreatedAt < keep && finished.Contains(d.Status))
                    .OrderBy(d => d.Id).Take(batch).ExecuteDeleteAsync(ct));
                // A request goes once nothing of it is left to do: its notification isn't still working, and none of its deliveries is unfinished.
                requests += await InBatchesAsync(() => db.Requests.Where(r => r.TenantId == tenant && r.AcceptedAt < keep
                        && !db.Notifications.Any(n => n.TenantId == tenant && n.RequestId == r.Id
                            && (n.State == NotificationState.Planning || n.State == NotificationState.Running))
                        && !db.DeliveryItems.Any(i => i.TenantId == tenant && i.RequestId == r.Id))
                    .OrderBy(r => r.Id).Take(batch).ExecuteDeleteAsync(ct));
                contents += await InBatchesAsync(() => db.Deliveries.Where(d => d.TenantId == tenant && d.CreatedAt < content && finished.Contains(d.Status)
                        && d.Content != "")
                    .OrderBy(d => d.Id).Take(batch).ExecuteUpdateAsync(s => s.SetProperty(d => d.Content, ""), ct));
                await db.Digests.Where(d => d.TenantId == tenant && d.State != DigestState.Open && d.ClosesAt < keep).ExecuteDeleteAsync(ct);
                await db.Storms.Where(s => s.TenantId == tenant && s.EndedAt != null && s.EndedAt < keep).ExecuteDeleteAsync(ct);
                await db.ProviderEvents.Where(e => e.TenantId == tenant && e.ReceivedAt < keep).ExecuteDeleteAsync(ct);
            }
            await db.LinkCodes.Where(l => l.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
            if (deliveries + requests + contents > 0)
            {
                logger.LogInformation("Retention removed {Deliveries} deliveries and {Requests} requests, and cleared the content of {Contents}.", deliveries,
                    requests, contents);
            }
            return (deliveries, requests, contents);
        }

        /// <summary>Runs a batch statement until it touches fewer rows than a batch.</summary>
        private async Task<int> InBatchesAsync(Func<Task<int>> statement)
        {
            var total = 0;
            int done;
            do
            {
                done = await statement();
                total += done;
            }
            while (done >= options.Value.Batch);
            return total;
        }
    }
}
