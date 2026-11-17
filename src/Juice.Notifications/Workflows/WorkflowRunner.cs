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
    public sealed class WorkflowRunner(People.MuteMatches mutes, ILogger<WorkflowRunner> logger)
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
                        if (!condition.When.Holds(request.Labels, CorrelationState(request)))
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
                }
                row.WorkflowStep++;
            }
            row.State = NotificationState.Planned;
            context.Done(row, null);
        }

        /// <summary>
        /// The correlation's state as conditions read it. Until correlations have a lifecycle (phase-3-plan M20) a request with a key is
        /// <c>open</c>; one without has none.
        /// </summary>
        private static string? CorrelationState(NotificationRequest request) => request.CorrelationKey is null ? null : CorrelationStates.Open;

        /// <summary>
        /// The waiting recipients become due for the delivery planner, past the tenant's mutes and the people's preferences as at planning
        /// (design §6.4): those can have changed while the workflow waited.
        /// </summary>
        private async Task SendAsync(StageContext context, Notification row, NotificationRequest request, NotificationType type, CancellationToken ct)
        {
            var db = context.Db;
            var waiting = await db.Recipients.Where(r => r.TenantId == row.TenantId && r.NotificationId == row.Id && r.Outcome == RecipientOutcome.Pending)
                .ToListAsync(ct);
            if (waiting.Count == 0)
            {
                return;
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
                .Where(r => r.TenantId == row.TenantId && r.NotificationId == row.Id && r.Outcome == RecipientOutcome.Pending).ToListAsync(ct);
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

        /// <summary>What a throttle counts by, for this request: long values (a label) are hashed to fit the column.</summary>
        internal static string ThrottleKey(string by, NotificationRequest request)
        {
            var value = by switch
            {
                ThrottleKeys.Type => "",
                ThrottleKeys.Correlation => request.CorrelationKey ?? "",
                _ => request.Labels.GetValueOrDefault(by[ThrottleKeys.LabelPrefix.Length..]) ?? "",
            };
            return value.Length <= 200 ? value : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }
}
