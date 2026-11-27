using Juice.Notifications.Data;
using Juice.Notifications.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Correlations
{
    // Correlations (design §5.3, phase-3-plan I32): an application's requests that share a key, with a lifecycle the producer drives. Resolving
    // or cancelling stops what is still due, in the same transaction as the state change, and every person's item shows the new state.

    public sealed record CorrelationNotificationView(Guid Id, Guid RequestId, string Type, NotificationState State, DateTimeOffset CreatedAt);

    public sealed record CorrelationView(string Key, CorrelationState State, string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt, DateTimeOffset? ResolvedAt,
        string? ResolvedBy, string? Outcome, DateTimeOffset? ReopenedAt, DateTimeOffset CreatedAt, DateTimeOffset LastRequestAt,
        IReadOnlyList<CorrelationNotificationView> Notifications, IReadOnlyDictionary<string, int> Recipients, IReadOnlyDictionary<string, int> Deliveries,
        DateTimeOffset? HeldAt, string? HoldReason, string? HeldBy);

    public static class CorrelationLimits
    {
        public const int MaxBy = 200;
        public const int MaxOutcome = 500;
        /// <summary>Notifications a correlation's view lists, newest first.</summary>
        public const int MaxListed = 100;

        public static bool IsOpen(CorrelationState state) => state is CorrelationState.Open or CorrelationState.Acknowledged;

        /// <summary>As conditions name it (<c>open</c>, <c>acknowledged</c> …).</summary>
        public static string Name(CorrelationState state) => state.ToString().ToLowerInvariant();
    }

    /// <summary>A hold or release after the call: the correlation as it stands, whether the call changed it, and the parked escalations it made due.</summary>
    public sealed record CorrelationHoldResult(CorrelationView View, bool Changed, int Resumed);

    /// <summary>What a hold or a release does to a correlation (phase-5-plan p5-d3; m44-architecture §4.1).</summary>
    public enum HoldDecision { Invalid, None, Hold, ReplaceReason, Release }

    /// <summary>
    /// The hold rules, pure. The correlation's state gates neither call: a hold stands until the producer releases it, whatever happens to the
    /// correlation (resolve, cancel, reopen), because the producer decides suppression (D13).
    /// </summary>
    public static class HoldRules
    {
        public const int MaxReason = 200;

        public static bool IsReason(string? reason) => Types.TypeRules.IsLabel(reason, MaxReason);

        /// <summary>Unheld: hold. Held for this reason: no change. Held for another: the reason is replaced, its start and who held it kept.</summary>
        public static HoldDecision Hold(DateTimeOffset? heldAt, string? holdReason, string? reason)
            => !IsReason(reason) ? HoldDecision.Invalid
                : heldAt is null ? HoldDecision.Hold
                : string.Equals(holdReason, reason, StringComparison.Ordinal) ? HoldDecision.None
                : HoldDecision.ReplaceReason;

        /// <summary>Held: release. Not held: no change.</summary>
        public static HoldDecision Release(DateTimeOffset? heldAt) => heldAt is null ? HoldDecision.None : HoldDecision.Release;
    }

    public static class CorrelationBook
    {
        /// <summary>
        /// The correlation for a request being accepted, tracked: created if it isn't there (an upsert committed on its own, safe to repeat, so two
        /// first requests at once make one), reopened if it was resolved or cancelled (design §19 decision 14), and touched either way, so the
        /// request's save carries the correlation's row version: a resolve committed meanwhile makes the save fail, and the request is retried
        /// rather than accepted on a correlation it didn't see closed.
        /// </summary>
        public static async Task<Correlation> ForRequestAsync(NotificationsDbContext db, Guid tenantId, Guid applicationId, string key, DateTimeOffset now,
            CancellationToken ct)
        {
            var id = Guid.CreateVersion7();
            var open = CorrelationState.Open.ToString();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.correlations (tenant_id, id, application_id, key, state, created_at, last_request_at, updated_at)
                VALUES ({tenantId}, {id}, {applicationId}, {key}, {open}, {now}, {now}, {now})
                ON CONFLICT (tenant_id, application_id, key) DO NOTHING
                """, ct);
            var correlation = await db.Correlations.SingleAsync(c => c.TenantId == tenantId && c.ApplicationId == applicationId && c.Key == key, ct);
            if (!CorrelationLimits.IsOpen(correlation.State))
            {
                correlation.State = CorrelationState.Open;
                (correlation.AcknowledgedBy, correlation.AcknowledgedAt, correlation.ResolvedAt, correlation.ResolvedBy, correlation.Outcome) = (null, null, null, null, null);
                correlation.ReopenedAt = now;
            }
            correlation.LastRequestAt = now;
            correlation.UpdatedAt = now;
            return correlation;
        }
    }

    /// <summary>
    /// Acknowledge, resolve and cancel (design §5.3), for the producer's own correlations (D5). Not mediator commands: the state change and the
    /// cancelling of everything still due are one transaction, and bulk statements, which a command's handler can't run inside its transaction.
    /// </summary>
    public sealed class CorrelationLifecycle(NotificationsDbContext db, TimeProvider clock, IInboxEvents inbox, Events.ProducerEvents events,
        ILogger<CorrelationLifecycle> logger, Stages.StageNudges? nudges = null)
    {
        public Task<Outcome<CorrelationView>> AcknowledgeAsync(Guid tenantId, Guid applicationId, string key, string? by, CancellationToken ct)
            => ChangeAsync(tenantId, applicationId, key, by, null, ct, (c, now) => c.State switch
            {
                CorrelationState.Open => Acknowledge(c, by, now),
                CorrelationState.Acknowledged => Change.None,
                _ => Change.Closed,
            });

        public Task<Outcome<CorrelationView>> ResolveAsync(Guid tenantId, Guid applicationId, string key, string? outcome, string? by, CancellationToken ct)
            => ChangeAsync(tenantId, applicationId, key, by, outcome, ct, (c, now) => c.State switch
            {
                CorrelationState.Open or CorrelationState.Acknowledged
                    => Set(c, () => (c.State, c.ResolvedAt, c.ResolvedBy, c.Outcome) = (CorrelationState.Resolved, now, by, outcome), stops: true),
                CorrelationState.Resolved => Change.None,
                _ => Change.Closed,
            });

        public Task<Outcome<CorrelationView>> CancelAsync(Guid tenantId, Guid applicationId, string key, string? by, CancellationToken ct)
            => ChangeAsync(tenantId, applicationId, key, by, null, ct, (c, now) => c.State switch
            {
                CorrelationState.Open or CorrelationState.Acknowledged
                    => Set(c, () => (c.State, c.ResolvedAt, c.ResolvedBy, c.Outcome) = (CorrelationState.Cancelled, now, by, null), stops: true),
                CorrelationState.Cancelled => Change.None,
                _ => Change.Closed,
            });

        /// <summary>
        /// Holds the correlation's escalation (p5-d3): from the commit on, a step of it that falls due is parked by the escalation stage until the
        /// release. In any state (the hold stands until released). Every running escalation of it is fenced (its version moves on and its claim is
        /// cleared, m44-architecture §6.3), so a stage worker part way through a step it read unheld loses its save, and parks on its next pass.
        /// Again with the same reason writes nothing.
        /// </summary>
        public Task<Outcome<CorrelationHoldResult>> HoldAsync(Guid tenantId, Guid applicationId, string key, string? reason, string? by, CancellationToken ct)
        {
            if (!HoldRules.IsReason(reason))
            {
                return Task.FromResult(Outcome<CorrelationHoldResult>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/reason", $"Why it is held is 1–{HoldRules.MaxReason} characters, no control characters or surrounding spaces.")]));
            }
            return HoldChangeAsync(tenantId, applicationId, key, by, ct, async (c, now) =>
            {
                var decision = HoldRules.Hold(c.HeldAt, c.HoldReason, reason);
                if (decision == HoldDecision.Hold)
                {
                    (c.HeldAt, c.HoldReason, c.HeldBy) = (now, reason, by);
                }
                else if (decision == HoldDecision.ReplaceReason)
                {
                    c.HoldReason = reason; // the hold goes on: since when, and who held it, stay
                }
                else
                {
                    return null;
                }
                await SaveAndFenceAsync(c, now, resume: false, ct);
                logger.LogInformation("Correlation {Key} of application {Application} in tenant {Tenant} held ({Reason})", key, applicationId, tenantId, reason);
                return 0;
            });
        }

        /// <summary>
        /// Releases the hold: every parked escalation of the correlation is due now, and runs through the stage's checks as any due step does (a
        /// correlation closed meanwhile stops it). The other running ones are fenced as for a hold. Not held: no change. The escalation stage is
        /// woken once this commits.
        /// </summary>
        public Task<Outcome<CorrelationHoldResult>> ReleaseAsync(Guid tenantId, Guid applicationId, string key, string? by, CancellationToken ct)
            => HoldChangeAsync(tenantId, applicationId, key, by, ct, async (c, now) =>
            {
                if (HoldRules.Release(c.HeldAt) != HoldDecision.Release)
                {
                    return null;
                }
                (c.HeldAt, c.HoldReason, c.HeldBy) = (null, null, null);
                var resumed = await SaveAndFenceAsync(c, now, resume: true, ct);
                logger.LogInformation("Correlation {Key} of application {Application} in tenant {Tenant} released: {Resumed} escalations resumed",
                    key, applicationId, tenantId, resumed);
                return resumed;
            });

        /// <summary>
        /// Saves the correlation (its row version: a request, a resolve or another hold at the same moment can't both commit), then fences its
        /// running escalations: each one's version moves on and its claim is cleared, and on a release the parked ones are due now. Returns how many
        /// were parked and are due again.
        /// </summary>
        private async Task<int> SaveAndFenceAsync(Correlation c, DateTimeOffset now, bool resume, CancellationToken ct)
        {
            c.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            var running = EscalationState.Running.ToString();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE notifications.escalations e
                SET claimed_by = NULL, lease_until = NULL, version = e.version + 1, updated_at = {now}
                WHERE e.tenant_id = {c.TenantId} AND e.correlation_id = {c.Id} AND e.state = {running}
                """, ct);
            if (!resume)
            {
                return 0;
            }
            return await db.Database.ExecuteSqlAsync($"""
                UPDATE notifications.escalations e
                SET next_at = {now}, held_at = NULL, claimed_by = NULL, lease_until = NULL, version = e.version + 1, updated_at = {now}
                WHERE e.tenant_id = {c.TenantId} AND e.correlation_id = {c.Id} AND e.state = {running} AND e.held_at IS NOT NULL
                """, ct);
        }

        /// <summary>
        /// A hold or release in one transaction: its own, or the host's it was called in (embedded), which the host commits. <paramref name="change"/>
        /// returns null for no change (nothing written; its own transaction rolled back) or the escalations it resumed.
        /// </summary>
        private async Task<Outcome<CorrelationHoldResult>> HoldChangeAsync(Guid tenantId, Guid applicationId, string key, string? by, CancellationToken ct,
            Func<Correlation, DateTimeOffset, Task<int?>> change)
        {
            if (by is not null && !Types.TypeRules.IsLabel(by, CorrelationLimits.MaxBy))
            {
                return Outcome<CorrelationHoldResult>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/by", $"Who did it is 1–{CorrelationLimits.MaxBy} characters, no control characters or surrounding spaces.")]);
            }
            // To the microsecond, as PostgreSQL keeps it: the hold this answers with is the one a later read shows.
            var now = clock.GetUtcNow();
            now = now.AddTicks(-(now.Ticks % 10));
            var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            await using (own)
            {
                var correlation = await db.Correlations.SingleOrDefaultAsync(c => c.TenantId == tenantId && c.ApplicationId == applicationId && c.Key == key, ct);
                if (correlation is null)
                {
                    return Outcome<CorrelationHoldResult>.NotFound($"There is no correlation {key}.");
                }
                var resumed = await change(correlation, now);
                if (resumed is null)
                {
                    if (own is not null)
                    {
                        await own.RollbackAsync(ct);
                    }
                    return Outcome<CorrelationHoldResult>.Ok(new CorrelationHoldResult(await ViewAsync(db, correlation, ct), false, 0));
                }
                if (own is not null)
                {
                    await own.CommitAsync(ct);
                    if (resumed > 0)
                    {
                        // Committed: the stage takes the resumed steps now rather than at its next poll. Embedded, the client does both once the
                        // host commits.
                        Telemetry.EscalationsReleased.Add(resumed.Value);
                        nudges?.Wake(Workflows.EscalationStage.StageName);
                    }
                }
                return Outcome<CorrelationHoldResult>.Ok(new CorrelationHoldResult(await ViewAsync(db, correlation, ct), true, resumed.Value));
            }
        }

        private enum Change { None, Changed, Acknowledges, Stops, Closed }

        private static Change Acknowledge(Correlation c, string? by, DateTimeOffset now)
        {
            (c.State, c.AcknowledgedBy, c.AcknowledgedAt) = (CorrelationState.Acknowledged, by, now);
            return Change.Acknowledges;
        }

        private static Change Set(Correlation c, Action apply, bool stops = false)
        {
            apply();
            return stops ? Change.Stops : Change.Changed;
        }

        private async Task<Outcome<CorrelationView>> ChangeAsync(Guid tenantId, Guid applicationId, string key, string? by, string? outcome, CancellationToken ct,
            Func<Correlation, DateTimeOffset, Change> change)
        {
            if (by is not null && !Types.TypeRules.IsLabel(by, CorrelationLimits.MaxBy))
            {
                return Outcome<CorrelationView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/by", $"Who did it is 1–{CorrelationLimits.MaxBy} characters, no control characters or surrounding spaces.")]);
            }
            if (outcome is not null && !Types.TypeRules.IsLabel(outcome, CorrelationLimits.MaxOutcome))
            {
                return Outcome<CorrelationView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/outcome", $"The outcome is 1–{CorrelationLimits.MaxOutcome} characters, no control characters or surrounding spaces.")]);
            }
            var now = clock.GetUtcNow();
            List<(Guid Subscriber, Guid Item)> items;
            // Its own transaction; or, embedded, the host's one it was called in (phase-5-plan I59), which the host commits.
            var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            await using (own)
            {
                var correlation = await db.Correlations.SingleOrDefaultAsync(c => c.TenantId == tenantId && c.ApplicationId == applicationId && c.Key == key, ct);
                if (correlation is null)
                {
                    return Outcome<CorrelationView>.NotFound($"There is no correlation {key}.");
                }
                var result = change(correlation, now);
                if (result == Change.Closed)
                {
                    return Outcome<CorrelationView>.Conflict(ErrorCodes.CorrelationClosed,
                        $"The correlation {key} is {CorrelationLimits.Name(correlation.State)}; a new request reopens it.");
                }
                if (result == Change.None)
                {
                    if (own is not null)
                    {
                        await own.RollbackAsync(ct);
                    }
                    return Outcome<CorrelationView>.Ok(await ViewAsync(db, correlation, ct));
                }
                correlation.UpdatedAt = now;
                // The producer hears it (phase-3-plan I37), committed with the change.
                var state = correlation.State;
                await events.RaiseAsync(db, tenantId, applicationId, app => new Events.NotificationEvent
                {
                    Event = state switch
                    {
                        CorrelationState.Acknowledged => Events.ProducerEventNames.CorrelationAcknowledged,
                        CorrelationState.Resolved => Events.ProducerEventNames.CorrelationResolved,
                        _ => Events.ProducerEventNames.CorrelationCancelled,
                    },
                    Application = app, NotificationTenantId = tenantId, CorrelationKey = key, State = CorrelationLimits.Name(state),
                    By = state == CorrelationState.Acknowledged ? correlation.AcknowledgedBy : correlation.ResolvedBy, Outcome = correlation.Outcome, OccurredAt = now,
                }, ct);
                // The row version: a request accepted at this moment (which touches it) and this change can't both commit.
                await db.SaveChangesAsync(ct);
                if (result == Change.Acknowledges)
                {
                    // Escalations whose policy stops on acknowledgement stop now (phase 3 proof point 2); the others go on.
                    var running = EscalationState.Running.ToString();
                    var stopped = EscalationState.Stopped.ToString();
                    const string acknowledged = "acknowledged";
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE notifications.escalations e
                        SET state = {stopped}, stop_reason = {acknowledged}, next_at = NULL, held_at = NULL, claimed_by = NULL, lease_until = NULL, version = e.version + 1, updated_at = {now}
                        FROM notifications.escalation_policies p
                        WHERE e.tenant_id = {tenantId} AND e.correlation_id = {correlation.Id} AND e.state = {running}
                          AND p.tenant_id = e.tenant_id AND p.id = e.policy_id AND p.stop_on_acknowledge
                        """, ct);
                }
                if (result == Change.Stops)
                {
                    var (notifications, recipients, deliveries) = await StopAsync(db, tenantId, correlation.Id, now, ct);
                    logger.LogInformation("Correlation {Key} of application {Application} in tenant {Tenant} {State}: {Notifications} notifications, {Recipients} recipients and {Deliveries} deliveries cancelled",
                        key, applicationId, tenantId, correlation.State, notifications, recipients, deliveries);
                }
                items = await db.InboxItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.CorrelationId == correlation.Id)
                    .Select(i => new { i.SubscriberId, i.Id }).ToListAsync(ct) is var rows ? rows.Select(r => (r.SubscriberId, r.Id)).ToList() : [];
                if (own is null)
                {
                    // The host commits it, later. Only the embedded host calls in a transaction of its own, and it has no inbox: no item to tell.
                    return Outcome<CorrelationView>.Ok(await ViewAsync(db, correlation, ct));
                }
                await own.CommitAsync(ct);
                var view = await ViewAsync(db, correlation, ct);
                // Committed: every person with an item of it sees the new state (I35).
                foreach (var person in items.GroupBy(i => i.Subscriber))
                {
                    await inbox.PublishAsync(new InboxChange(tenantId, person.Key, InboxChange.Changed, [.. person.Select(i => i.Item)]), CancellationToken.None);
                }
                return Outcome<CorrelationView>.Ok(view);
            }
        }

        /// <summary>
        /// Cancels what is still due for the correlation (phase-3-plan I32): notifications still planning or running their workflow, recipients
        /// not yet taken by the delivery planner, and deliveries waiting to be sent that no dispatcher holds (one being sent finishes and is
        /// recorded: it may have gone out). Each row's version moves on, so a worker part way through a step on it loses its save rather than
        /// undoing the cancel; the dispatcher also checks the correlation before it sends.
        /// </summary>
        internal static async Task<(int Notifications, int Recipients, int Deliveries)> StopAsync(NotificationsDbContext db, Guid tenantId, Guid correlationId,
            DateTimeOffset now, CancellationToken ct)
        {
            var (cancelled, planning, running, retrying) = (NotificationState.Cancelled.ToString(), NotificationState.Planning.ToString(),
                NotificationState.Running.ToString(), DeliveryStatus.Retrying.ToString());
            var stillPlanned = RecipientPlanning.StillPlannedNames.ToArray(); // an array for ANY()
            var recipientCancelled = RecipientOutcome.Cancelled.ToString();
            var deliveryCancelled = DeliveryStatus.Cancelled.ToString();
            var deliveryPending = DeliveryStatus.Pending.ToString();
            var (resolved, closed) = (CorrelationState.Resolved.ToString(), CorrelationState.Cancelled.ToString());
            const string reason = "The correlation was resolved or cancelled before it was sent.";
            var recipients = await db.Database.ExecuteSqlAsync($"""
                UPDATE notifications.notification_recipients x
                SET outcome = {recipientCancelled}, next_at = NULL, claimed_by = NULL, lease_until = NULL, version = x.version + 1
                FROM notifications.notifications n
                JOIN notifications.requests r ON r.tenant_id = n.tenant_id AND r.id = n.request_id
                WHERE x.tenant_id = {tenantId} AND n.tenant_id = x.tenant_id AND n.id = x.notification_id AND r.correlation_id = {correlationId}
                  AND (x.outcome = ANY({stillPlanned}) OR x.next_at IS NOT NULL) -- RecipientPlanning: more could still be planned for them
                """, ct);
            var notifications = await db.Database.ExecuteSqlAsync($"""
                UPDATE notifications.notifications n
                SET state = {cancelled}, next_at = NULL, claimed_by = NULL, lease_until = NULL, version = n.version + 1, step_entered_at = NULL, updated_at = {now}
                FROM notifications.requests r
                WHERE n.tenant_id = {tenantId} AND r.tenant_id = n.tenant_id AND r.id = n.request_id AND r.correlation_id = {correlationId}
                  AND n.state IN ({planning}, {running})
                """, ct);
            var deliveries = await db.Database.ExecuteSqlAsync($"""
                UPDATE notifications.deliveries d
                SET status = {deliveryCancelled}, next_at = NULL, completed_at = {now}, last_error = {reason}, version = d.version + 1, failure_check_due = TRUE, tenant_held = FALSE
                WHERE d.tenant_id = {tenantId} AND d.status IN ({deliveryPending}, {retrying}) AND (d.lease_until IS NULL OR d.lease_until <= {now})
                  AND EXISTS (SELECT 1 FROM notifications.delivery_items i JOIN notifications.requests r ON r.tenant_id = i.tenant_id AND r.id = i.request_id
                              WHERE i.tenant_id = d.tenant_id AND i.delivery_id = d.id AND r.correlation_id = {correlationId})
                  -- A digest carrying another request with no correlation, or one still open, goes out: only what is wholly closed is cancelled.
                  AND NOT EXISTS (SELECT 1 FROM notifications.delivery_items i JOIN notifications.requests r ON r.tenant_id = i.tenant_id AND r.id = i.request_id
                                  LEFT JOIN notifications.correlations c ON c.tenant_id = r.tenant_id AND c.id = r.correlation_id
                                  WHERE i.tenant_id = d.tenant_id AND i.delivery_id = d.id AND r.correlation_id IS DISTINCT FROM {correlationId}
                                    AND (r.correlation_id IS NULL OR c.state NOT IN ({resolved}, {closed})))
                """, ct);
            // Its escalations end here: nobody more is added.
            var (escalating, ended) = (EscalationState.Running.ToString(), EscalationState.Stopped.ToString());
            var stopReason = await db.Correlations.AsNoTracking().Where(c => c.TenantId == tenantId && c.Id == correlationId).Select(c => c.State).SingleAsync(ct)
                == CorrelationState.Resolved ? "resolved" : "cancelled";
            await db.Database.ExecuteSqlAsync($"""
                UPDATE notifications.escalations e
                SET state = {ended}, stop_reason = {stopReason}, next_at = NULL, held_at = NULL, claimed_by = NULL, lease_until = NULL, version = e.version + 1, updated_at = {now}
                WHERE e.tenant_id = {tenantId} AND e.correlation_id = {correlationId} AND e.state = {escalating}
                """, ct);
            return (notifications, recipients, deliveries);
        }

        internal static async Task<CorrelationView> ViewAsync(NotificationsDbContext db, Correlation c, CancellationToken ct)
        {
            var notifications = await (from n in db.Notifications
                                       join r in db.Requests on new { n.TenantId, Id = n.RequestId } equals new { r.TenantId, r.Id }
                                       where r.TenantId == c.TenantId && r.CorrelationId == c.Id
                                       orderby n.Id descending
                                       select new CorrelationNotificationView(n.Id, r.Id, r.TypeKey, n.State, n.CreatedAt))
                .Take(CorrelationLimits.MaxListed).AsNoTracking().ToListAsync(ct);
            var recipients = await (from x in db.Recipients
                                    join n in db.Notifications on new { x.TenantId, Id = x.NotificationId } equals new { n.TenantId, n.Id }
                                    join r in db.Requests on new { n.TenantId, Id = n.RequestId } equals new { r.TenantId, r.Id }
                                    where r.TenantId == c.TenantId && r.CorrelationId == c.Id
                                    group x by x.Outcome into g
                                    select new { g.Key, Count = g.Count() }).AsNoTracking().ToListAsync(ct);
            var deliveries = await (from d in db.Deliveries
                                    where d.TenantId == c.TenantId && db.DeliveryItems.Any(i => i.TenantId == d.TenantId && i.DeliveryId == d.Id
                                        && db.Requests.Any(r => r.TenantId == i.TenantId && r.Id == i.RequestId && r.CorrelationId == c.Id))
                                    group d by d.Status into g
                                    select new { g.Key, Count = g.Count() }).AsNoTracking().ToListAsync(ct);
            return new CorrelationView(c.Key, c.State, c.AcknowledgedBy, c.AcknowledgedAt, c.ResolvedAt, c.ResolvedBy, c.Outcome, c.ReopenedAt, c.CreatedAt,
                c.LastRequestAt, notifications,
                recipients.ToDictionary(r => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(r.Key.ToString()), r => r.Count),
                deliveries.ToDictionary(d => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(d.Key.ToString()), d => d.Count),
                c.HeldAt, c.HoldReason, c.HeldBy);
        }

        /// <summary>The producer's correlation, as it stands.</summary>
        public async Task<CorrelationView?> GetAsync(Guid tenantId, Guid applicationId, string key, CancellationToken ct)
            => await db.Correlations.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenantId && c.ApplicationId == applicationId && c.Key == key, ct)
                is { } correlation ? await ViewAsync(db, correlation, ct) : null;
    }
}
