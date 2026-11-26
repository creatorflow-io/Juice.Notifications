using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Juice.Notifications.Data;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Workflows
{
    /// <summary>
    /// Runs a notification's workflow (design §7.1, phase-3-plan I27–I30) inside the planner's step: from <see cref="Notification.WorkflowStep"/>,
    /// step after step, until one waits (the notification is due again when it may go on) or the workflow ends (<c>Planned</c>). The planner
    /// stage claims notifications in both <c>Planning</c> and <c>Running</c>, so there is one claimed hop per wait, crash-safe like every other.
    /// </summary>
    public sealed class WorkflowRunner(People.MuteMatches mutes, ILogger<WorkflowRunner> logger, Storms.StormGuard? storms = null)
    {
        /// <summary>Parsed workflows by type version: a version's workflow never changes.</summary>
        private readonly ConcurrentDictionary<Guid, Workflow> _parsed = new();

        public Workflow For(NotificationType type)
            => type.Workflow == Workflow.SendOnlyJson ? Workflow.SendOnly : _parsed.GetOrAdd(type.Id, _ => Workflow.Read(type.Workflow));

        public async Task RunAsync(StageContext context, Notification row, NotificationRequest request, CancellationToken ct)
        {
            var db = context.Db;
            var now = context.Now;
            if (request.ExpiresAt is { } expires && expires <= now)
            {
                await EndAsync(context, row, NotificationState.Expired, ct);
                return;
            }
            var type = await db.Types.AsNoTracking().SingleAsync(t => t.Id == request.TypeId, ct);
            var workflow = For(type);
            while (row.WorkflowStep < workflow.Steps.Count)
            {
                switch (workflow.Steps[row.WorkflowStep])
                {
                    case SendStep:
                        await SendAsync(context, row, request, type, ct);
                        break;
                    case DelayStep delay:
                        if (row.StepEnteredAt is null)
                        {
                            row.StepEnteredAt = now;
                            context.Done(row, now + delay.Delay);
                            return;
                        }
                        if (row.StepEnteredAt.Value + delay.Delay > now)
                        {
                            context.Done(row, row.StepEnteredAt.Value + delay.Delay); // claimed early (a nudge): not yet
                            return;
                        }
                        row.StepEnteredAt = null;
                        break;
                    case ConditionStep condition:
                        if (!condition.When.Holds(request.Labels, await CorrelationStateAsync(db, request, ct)))
                        {
                            logger.LogDebug("Notification {Id} in tenant {Tenant}: condition at step {Step} doesn't hold", row.Id, row.TenantId, row.WorkflowStep);
                            await EndAsync(context, row, NotificationState.ConditionNotMet, ct);
                            return;
                        }
                        break;
                    case ThrottleStep throttle:
                        if (await ThrottleAsync(db, row, request, throttle, now, ct) is { } nextWindow)
                        {
                            context.Done(row, nextWindow);
                            return;
                        }
                        break;
                    case EscalateStep escalate:
                        await StartEscalationAsync(db, row, request, escalate, now, ct);
                        break;
                    case DigestStep digest:
                        await JoinDigestAsync(db, row, request, digest, now, ct);
                        // Its send is the digest's: past both (the declaration guarantees the send follows).
                        row.WorkflowStep++;
                        break;
                }
                row.WorkflowStep++;
            }
            row.State = NotificationState.Planned;
            context.Done(row, null);
        }

        /// <summary>The correlation's state as conditions read it (<c>open</c> …), now; none for a request without a key.</summary>
        private static async Task<string?> CorrelationStateAsync(NotificationsDbContext db, NotificationRequest request, CancellationToken ct)
            => request.CorrelationId is { } id
                ? Correlations.CorrelationLimits.Name(await db.Correlations.AsNoTracking().Where(c => c.TenantId == request.TenantId && c.Id == id)
                    .Select(c => c.State).SingleAsync(ct))
                : null;

        /// <summary>
        /// The waiting recipients become due for the delivery planner, past the tenant's mutes and the people's preferences as at planning
        /// (design §6.4): those can have changed while the workflow waited.
        /// </summary>
        private async Task SendAsync(StageContext context, Notification row, NotificationRequest request, NotificationType type, CancellationToken ct)
        {
            var db = context.Db;
            // Not those a digest holds (a step after "digest, send"): the digest sends them when it closes.
            var waiting = await db.Recipients.Where(r => r.TenantId == row.TenantId && r.NotificationId == row.Id && r.Outcome == RecipientOutcome.Pending
                    && r.DigestId == null)
                .ToListAsync(ct);
            if (waiting.Count == 0)
            {
                return;
            }
            // Storm protection (phase-4-plan I43): a route past its threshold takes its recipients as a one-minute digest instead.
            if (storms is not null && waiting.Any(r => r.RouteKey is not null))
            {
                var routes = waiting.Where(r => r.RouteKey is not null).GroupBy(r => r.RouteKey!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                var storming = await storms.CountAsync(db, row.TenantId, routes, context.Now, context.AfterCommit, ct);
                if (storming.Count > 0)
                {
                    var digested = waiting.Where(r => r.RouteKey is { } key && storming.Contains(key)).Select(r => r.Id).ToHashSet();
                    await JoinDigestAsync(db, row, request, new DigestStep(storms.Window, DigestKeys.Route), context.Now, ct, digested);
                    waiting = waiting.Where(r => !digested.Contains(r.Id)).ToList();
                    if (waiting.Count == 0)
                    {
                        return;
                    }
                }
            }
            foreach (var recipient in waiting)
            {
                (recipient.Outcome, recipient.NextAt) = (RecipientOutcome.Planned, context.Now);
            }
            await People.PreferencePolicy.ApplyAsync(db, request, type, waiting, mutes, context.Now, ct);
            var held = waiting.Where(r => r.Outcome != RecipientOutcome.Planned).GroupBy(r => r.Outcome).Select(g => (Outcome: g.Key.ToString(), Count: g.Count())).ToList();
            if (held.Count > 0)
            {
                context.AfterCommit.Add(() =>
                {
                    foreach (var (outcome, count) in held)
                    {
                        Telemetry.Recipients.Add(count, new KeyValuePair<string, object?>("outcome", outcome));
                    }
                });
            }
            if (waiting.Any(r => r.Outcome == RecipientOutcome.Planned))
            {
                context.Wakes.Add(DeliveryStageNames.DeliveryPlanner);
            }
        }

        /// <summary>Ends the workflow early: the notification's state, and its waiting recipients cancelled (none of them is sent).</summary>
        private static async Task EndAsync(StageContext context, Notification row, NotificationState state, CancellationToken ct)
        {
            var waiting = await context.Db.Recipients
                .Where(r => r.TenantId == row.TenantId && r.NotificationId == row.Id && r.Outcome == RecipientOutcome.Pending && r.DigestId == null).ToListAsync(ct);
            foreach (var recipient in waiting)
            {
                (recipient.Outcome, recipient.NextAt) = (RecipientOutcome.Cancelled, null);
            }
            row.State = state;
            row.StepEnteredAt = null;
            context.Done(row, null);
        }

        /// <summary>Advisory locks of throttles (a namespace of their own; the display group catalog has 0x1B0C).</summary>
        private const int ThrottleLockSpace = 0x1B0D;

        /// <summary>
        /// Counts the notification in its throttle's window, or answers when the next window starts if this one is full (I30). In its own short
        /// transaction under an advisory lock per tenant, type and key, so two workers can't both take the last place; idempotent per
        /// notification and step (a step run again after a lost lease counts once). Entries of windows long past are removed on the way.
        /// </summary>
        private static async Task<DateTimeOffset?> ThrottleAsync(NotificationsDbContext db, Notification row, NotificationRequest request, ThrottleStep throttle,
            DateTimeOffset now, CancellationToken ct)
        {
            var key = ThrottleKey(throttle.By, request);
            var windowTicks = throttle.Window.Ticks;
            var windowStart = new DateTimeOffset(now.UtcTicks - now.UtcTicks % windowTicks, TimeSpan.Zero);
            var tenant = row.TenantId;
            var step = row.WorkflowStep;
            var typeKey = request.TypeKey;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockKey = $"{tenant}:{typeKey}:{key}";
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ThrottleLockSpace}, hashtext({lockKey}))", ct);
            if (await db.ThrottleEntries.AnyAsync(e => e.TenantId == tenant && e.NotificationId == row.Id && e.Step == step, ct))
            {
                await transaction.CommitAsync(ct);
                return null;
            }
            var stale = windowStart - throttle.Window;
            await db.ThrottleEntries.Where(e => e.TenantId == tenant && e.TypeKey == typeKey && e.Key == key && e.WindowStart < stale).ExecuteDeleteAsync(ct);
            var count = await db.ThrottleEntries.CountAsync(e => e.TenantId == tenant && e.TypeKey == typeKey && e.Key == key && e.WindowStart == windowStart, ct);
            if (count >= throttle.Limit)
            {
                await transaction.CommitAsync(ct);
                return windowStart + throttle.Window;
            }
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.throttle_entries (tenant_id, notification_id, step, type_key, key, window_start)
                VALUES ({tenant}, {row.Id}, {step}, {typeKey}, {key}, {windowStart})
                """, ct);
            await transaction.CommitAsync(ct);
            return null;
        }

        /// <summary>
        /// Starts the notification's escalation (I34): the policy the step names (or a label names), its first step due after its wait. It is saved
        /// with the step, so a step run again finds it there. Without a correlation, or a policy by that key, there is nothing to escalate: said
        /// in the log, and the workflow goes on.
        /// </summary>
        private async Task StartEscalationAsync(NotificationsDbContext db, Notification row, NotificationRequest request, EscalateStep escalate,
            DateTimeOffset now, CancellationToken ct)
        {
            if (request.CorrelationId is not { } correlation)
            {
                logger.LogInformation("Notification {Id} in tenant {Tenant} has no correlation key, so its escalate step does nothing", row.Id, row.TenantId);
                return;
            }
            var key = escalate.Policy ?? request.Labels.GetValueOrDefault(escalate.PolicyFromLabel!);
            var policy = key is null ? null
                : await db.EscalationPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == row.TenantId && p.Key == key, ct);
            if (policy is null)
            {
                logger.LogWarning("Notification {Id} in tenant {Tenant} escalates by policy {Policy}, which the tenant doesn't have; it doesn't escalate",
                    row.Id, row.TenantId, key ?? $"(label {escalate.PolicyFromLabel} missing)");
                return;
            }
            if (await db.Escalations.AnyAsync(e => e.TenantId == row.TenantId && e.NotificationId == row.Id, ct)
                || db.Escalations.Local.Any(e => e.NotificationId == row.Id))
            {
                return;
            }
            var steps = Configuration.EscalationRules.Read(policy.Steps);
            db.Escalations.Add(new Escalation
            {
                TenantId = row.TenantId, NotificationId = row.Id, CorrelationId = correlation, PolicyId = policy.Id, NextStep = 0, State = EscalationState.Running,
                NextAt = FirstStepAt(request, steps[0].AfterMinutes, now), CreatedAt = now, UpdatedAt = now,
            });
        }

        /// <summary>
        /// When an escalation's first step is due (phase 5, M45-n): its wait from when the request's event happened (<c>occurredAt</c>, which
        /// intake always sets: the acceptance time when the producer didn't say), and never before now. A request from before it was kept has
        /// none, and waits from now, as before. A request sent late (held under a silence) gets the step at once if its
        /// wait has passed, as legacy escalation does; later steps stay relative to the step before.
        /// </summary>
        internal static DateTimeOffset FirstStepAt(NotificationRequest request, double afterMinutes, DateTimeOffset now)
        {
            var wait = TimeSpan.FromMinutes(afterMinutes);
            if (request.OccurredAt is not { } occurred)
            {
                return now + wait;
            }
            var due = occurred + wait;
            return due > now ? due : now;
        }

        /// <summary>Advisory locks of digests: joining one and closing it take the same lock per tenant, type, grouping and key.</summary>
        internal const int DigestLockSpace = 0x1B0E;

        internal static string DigestLock(Guid tenant, string typeKey, string by, string key) => $"{tenant}:{typeKey}:{by}:{key}";

        /// <summary>
        /// Puts the notification's waiting recipients in the open digest for their key (I31), creating it (closing a window from now) if there is
        /// none: per key, in its own short transaction under the digest's lock, committed at once, so a closing digest either has them or another
        /// opens for them. A recipient already in one (this step run again) stays where it is.
        /// </summary>
        private static async Task JoinDigestAsync(NotificationsDbContext db, Notification row, NotificationRequest request, DigestStep digest, DateTimeOffset now,
            CancellationToken ct, IReadOnlySet<Guid>? only = null)
        {
            var waiting = (await db.Recipients.AsNoTracking()
                .Where(r => r.TenantId == row.TenantId && r.NotificationId == row.Id && r.Outcome == RecipientOutcome.Pending && r.DigestId == null)
                .Select(r => new { r.Id, r.RouteKey, r.SubscriberId, r.NamedAs }).ToListAsync(ct))
                .Where(r => only is null || only.Contains(r.Id)).ToList();
            var open = DigestState.Open.ToString();
            foreach (var group in waiting.GroupBy(r => Fit(digest.By switch
            {
                DigestKeys.Route => r.RouteKey ?? r.NamedAs,
                DigestKeys.Subscriber => r.SubscriberId?.ToString() ?? r.NamedAs,
                DigestKeys.Correlation => request.CorrelationKey ?? "",
                _ => "",
            }), StringComparer.Ordinal))
            {
                var key = group.Key;
                var ids = group.Select(r => r.Id).ToList();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var lockKey = DigestLock(row.TenantId, request.TypeKey, digest.By, key);
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({DigestLockSpace}, hashtext({lockKey}))", ct);
                var digestId = await db.Digests.Where(d => d.TenantId == row.TenantId && d.TypeKey == request.TypeKey && d.By == digest.By && d.Key == key
                    && d.State == DigestState.Open).Select(d => (Guid?)d.Id).SingleOrDefaultAsync(ct);
                if (digestId is null)
                {
                    digestId = Guid.CreateVersion7();
                    var closes = now + digest.Window;
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO notifications.digests (tenant_id, id, type_key, "by", key, state, opens_at, closes_at, members, version, next_at, step_failures)
                        VALUES ({row.TenantId}, {digestId}, {request.TypeKey}, {digest.By}, {key}, {open}, {now}, {closes}, 0, 0, {closes}, 0)
                        """, ct);
                }
                await db.Recipients.Where(r => r.TenantId == row.TenantId && ids.Contains(r.Id) && r.DigestId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.DigestId, digestId), ct);
                await transaction.CommitAsync(ct);
            }
        }

        internal static string Fit(string value)
            => value.Length <= 200 ? value : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        /// <summary>What a throttle counts by, for this request: long values (a label) are hashed to fit the column.</summary>
        internal static string ThrottleKey(string by, NotificationRequest request)
        {
            var value = by switch
            {
                ThrottleKeys.Type => "",
                ThrottleKeys.Correlation => request.CorrelationKey ?? "",
                _ => request.Labels.GetValueOrDefault(by[ThrottleKeys.LabelPrefix.Length..]) ?? "",
            };
            return Fit(value);
        }
    }
}
