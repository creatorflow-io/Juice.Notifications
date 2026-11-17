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
    public sealed class EscalationStage(ILogger<EscalationStage> logger) : IStage<Escalation>
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
                Exhaust(context, row);
                return;
            }
            var notification = await db.Notifications.SingleAsync(n => n.TenantId == row.TenantId && n.Id == row.NotificationId, ct);
            if (notification.State is NotificationState.Planning or NotificationState.Running)
            {
                context.Done(row, now + Busy);
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
                Exhaust(context, row);
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
            logger.LogInformation("Escalation {Escalation} in tenant {Tenant} stopped: {Reason}", row.Id, row.TenantId, reason);
            context.Done(row, null);
        }

        private void Exhaust(StageContext context, Escalation row)
        {
            (row.State, row.UpdatedAt) = (EscalationState.Exhausted, context.Now);
            logger.LogInformation("Escalation {Escalation} in tenant {Tenant} ran its last step", row.Id, row.TenantId);
            context.Done(row, null);
        }
    }
}
