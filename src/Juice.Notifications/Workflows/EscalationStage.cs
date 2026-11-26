using System.Text.Json;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Workflows
{
    /// <summary>
    /// Runs escalations (design §7.1, phase-3-plan I34): claims one when its next step is due and, while its correlation is still open (and not
    /// acknowledged, for a policy that stops on that), hands the step's recipients to the planner: the notification goes back to <c>Planning</c>
    /// with the step's number and names, and the planner resolves them through the tenant's resolvers, skips anyone the notification already
    /// reached, and sends the rest at once. The next step is due after its own wait; past the last, the escalation is exhausted. A step that
    /// reaches nobody is due again at once (the planner says so), so the next one runs without its wait.
    /// </summary>
    public sealed class EscalationStage(ILogger<EscalationStage> logger, Events.ProducerEvents? events = null) : IStage<Escalation>
    {
        public const string StageName = "escalation";

        /// <summary>How long a step waits when the notification is busy (its workflow or an earlier step still planning).</summary>
        private static readonly TimeSpan Busy = TimeSpan.FromSeconds(5);

        public string Name => StageName;

        public void GiveUp(StageContext context, Escalation row, string error)
        {
            Stop(context, row, "failed");
        }

        public async Task ExecuteAsync(StageContext context, Escalation row, CancellationToken ct)
        {
            var db = context.Db;
            var now = context.Now;
            if (row.State != EscalationState.Running)
            {
                context.Done(row, null);
                return;
            }
            var correlation = await db.Correlations.AsNoTracking().SingleAsync(c => c.TenantId == row.TenantId && c.Id == row.CorrelationId, ct);
            var policy = await db.EscalationPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == row.TenantId && p.Id == row.PolicyId, ct);
            if (policy is null)
            {
                Stop(context, row, "policy-deleted");
                return;
            }
            if (correlation.State is CorrelationState.Resolved or CorrelationState.Cancelled)
            {
                Stop(context, row, correlation.State == CorrelationState.Resolved ? "resolved" : "cancelled");
                return;
            }
            if (correlation.State == CorrelationState.Acknowledged && policy.StopOnAcknowledge)
            {
                Stop(context, row, "acknowledged");
                return;
            }
            var steps = EscalationRules.Read(policy.Steps);
            if (row.NextStep >= steps.Count)
            {
                await ExhaustAsync(context, row, steps.Count, ct);
                return;
            }
            if (correlation.HeldAt is not null)
            {
                // Held by its producer (p5-d3): the due step waits for the release, which makes it due again. Closed and acknowledged are
                // decided first, so a held escalation of a closed correlation stops rather than waits. The correlation was read in this step,
                // after the claim: a release committed since moved the row's version on, so this save is lost and the next pass runs the step.
                row.HeldAt ??= now;
                row.UpdatedAt = now;
                logger.LogInformation("Escalation {Escalation} of correlation {Correlation} in tenant {Tenant}: step {Step} held ({Reason})",
                    row.Id, correlation.Key, row.TenantId, row.NextStep + 1, correlation.HoldReason);
                context.Done(row, null);
                context.AfterCommit.Add(() => Telemetry.EscalationsHeld.Add(1));
                return;
            }
            row.HeldAt = null;
            var notification = await db.Notifications.SingleAsync(n => n.TenantId == row.TenantId && n.Id == row.NotificationId, ct);
            if (notification.State is NotificationState.Planning or NotificationState.Running)
            {
                // Its recipients are planned on the notification, which its own planning or workflow holds now: planning the step meanwhile would
                // end the workflow (its later steps lost). So it waits, until the workflow's own wait ends (a delay, a throttle's next window), not
                // polling through it: a step that falls due during a long delay runs when the delay ends (deferred.md #46).
                context.Done(row, notification.NextAt is { } due && due > now + Busy ? due : now + Busy);
                return;
            }
            if (notification.State != NotificationState.Planned)
            {
                Stop(context, row, notification.State.ToString().ToLowerInvariant()); // cancelled, expired, failed, or its condition didn't hold
                return;
            }
            // The planner takes it from here: these names, numbered as this step (its version moves on with the save, so a worker holding it fails).
            notification.EscalationStep = row.NextStep + 1;
            notification.EscalationSpecs = JsonSerializer.Serialize(steps[row.NextStep].Recipients, IntakeQueries.Json);
            notification.State = NotificationState.Planning;
            notification.NextAt = now;
            notification.UpdatedAt = now;
            logger.LogInformation("Escalation {Escalation} of correlation {Correlation} in tenant {Tenant}: step {Step} of {Steps} (policy {Policy})",
                row.Id, correlation.Key, row.TenantId, row.NextStep + 1, steps.Count, policy.Key);
            row.NextStep++;
            row.UpdatedAt = now;
            if (row.NextStep >= steps.Count)
            {
                await ExhaustAsync(context, row, steps.Count, ct);
            }
            else
            {
                context.Done(row, now + TimeSpan.FromMinutes(steps[row.NextStep].AfterMinutes));
            }
            context.Wakes.Add(PlannerStage.StageName);
        }

        private void Stop(StageContext context, Escalation row, string reason)
        {
            (row.State, row.StopReason, row.UpdatedAt) = (EscalationState.Stopped, reason, context.Now);
            row.HeldAt = null;
            logger.LogInformation("Escalation {Escalation} in tenant {Tenant} stopped: {Reason}", row.Id, row.TenantId, reason);
            context.Done(row, null);
        }

        /// <summary>Past its last step: done, and the producer hears it (<c>escalation.exhausted</c>, design §5.4), saved with the step.</summary>
        private async Task ExhaustAsync(StageContext context, Escalation row, int steps, CancellationToken ct)
        {
            (row.State, row.UpdatedAt) = (EscalationState.Exhausted, context.Now);
            row.HeldAt = null;
            logger.LogInformation("Escalation {Escalation} in tenant {Tenant} ran its last step", row.Id, row.TenantId);
            context.Done(row, null);
            if (events is { Enabled: true })
            {
                var request = await (from n in context.Db.Notifications
                                     join q in context.Db.Requests on new { n.TenantId, Id = n.RequestId } equals new { q.TenantId, q.Id }
                                     where n.TenantId == row.TenantId && n.Id == row.NotificationId
                                     select q).AsNoTracking().SingleAsync(ct);
                await events.RaiseAsync(context.Db, row.TenantId, request.ApplicationId, app => new Events.NotificationEvent
                {
                    Event = Events.ProducerEventNames.EscalationExhausted, Application = app, NotificationTenantId = row.TenantId,
                    CorrelationKey = request.CorrelationKey, NotificationId = row.NotificationId, RequestId = request.Id, Type = request.TypeKey, Steps = steps,
                    OccurredAt = context.Now,
                }, ct);
            }
        }
    }
}
