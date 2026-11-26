using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Planning;
using Juice.Notifications.Rendering;
using Juice.Notifications.Stages;
using Juice.Notifications.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Delivering
{
    /// <summary>
    /// Closes a digest when its window ends (design §7.1, D19; phase-3-plan I31) and plans its members in one step: the tenant's mutes and the
    /// people's preferences as they are now (as a <c>send</c> applies them), then the delivery planner's destinations, and <b>one delivery per
    /// destination and channel</b> with one item per member, its content rendered once with every notification it carries (<c>items</c>, the
    /// first 100, and <c>more</c>). A person's inbox still gets an item per notification: the inbox is never digested. A person in their quiet
    /// hours gets the digest's other deliveries when the hours end (the delivery waits, not the digest).
    /// </summary>
    /// <remarks>
    /// Closing takes the lock members join under, so none joins once it is closing. The close is committed on its own first; the planning is the
    /// step's save, and a step that runs again plans the members still waiting (the delivery items' unique key keeps any from going twice).
    /// </remarks>
    public sealed class DigestStage(DeliveryPlannerStage planner, IChannelRegistry channels, People.MuteMatches mutes, ILogger<DigestStage> logger)
        : IStage<Digest>
    {
        public const string StageName = "digest";

        public string Name => StageName;

        /// <summary>
        /// Closes an open digest: under the lock members join under (<see cref="WorkflowRunner.DigestLock"/>), in its own transaction committed
        /// at once, so none joins once it is closing. Synchronous for a give-up (the runner's hook is); <see cref="CloseAsync"/> for a step.
        /// </summary>
        private static void Close(NotificationsDbContext db, Digest row, DateTimeOffset now)
        {
            var (lockStatement, close) = Closing(row, now);
            using var transaction = db.Database.BeginTransaction();
            db.Database.ExecuteSql(lockStatement);
            var updated = db.Database.ExecuteSql(close);
            transaction.Commit();
            Closed(row, now, updated);
        }

        /// <inheritdoc cref="Close"/>
        private static async Task CloseAsync(NotificationsDbContext db, Digest row, DateTimeOffset now, CancellationToken ct)
        {
            var (lockStatement, close) = Closing(row, now);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync(lockStatement, ct);
            var updated = await db.Database.ExecuteSqlAsync(close, ct);
            await transaction.CommitAsync(ct);
            Closed(row, now, updated);
        }

        /// <summary>
        /// The close's result on the row. The close applies only at the version this worker loaded (its claim), so a stale worker's close lands
        /// on nothing and the owner's always applies. None updated: the row moved on since it was loaded (another worker claimed it), so this
        /// claim is lost. The step stops there, its row neither read back nor saved: the runner's version check then reports the lost lease,
        /// and nothing of this worker's lands over the owner's.
        /// </summary>
        private static void Closed(Digest row, DateTimeOffset now, int updated)
        {
            if (updated != 1)
            {
                throw new InvalidOperationException($"Digest {row.Id} moved on since this worker loaded it (claimed by another, perhaps closed); its claim is lost.");
            }
            (row.State, row.ClosedAt) = (DigestState.Closed, now);
        }

        private static (FormattableString Lock, FormattableString Close) Closing(Digest row, DateTimeOffset now)
        {
            var lockKey = WorkflowRunner.DigestLock(row.TenantId, row.TypeKey, row.By, row.Key);
            var (closed, open) = (DigestState.Closed.ToString(), DigestState.Open.ToString());
            return ($"SELECT pg_advisory_xact_lock({WorkflowRunner.DigestLockSpace}, hashtext({lockKey}))",
                $"UPDATE notifications.digests SET state = {closed}, closed_at = {now} WHERE tenant_id = {row.TenantId} AND id = {row.Id} AND state = {open} AND version = {row.Version}");
        }

        public void GiveUp(StageContext context, Digest row, string error)
        {
            // Nothing is sent from a digest that can't be planned: its waiting members fail with it (history shows them ended, and their requests
            // can be redacted), saved with the digest. An open one is closed first under the lock members join under, committed on its own as a
            // close is: none joins after its members are read, so none is left waiting in a digest that will never send. (Synchronous: the
            // runner's hook is.)
            if (row.State == DigestState.Open)
            {
                Close(context.Db, row, context.Now);
            }
            row.State = DigestState.Cancelled;
            foreach (var member in context.Db.Recipients.Where(r => r.TenantId == row.TenantId && r.DigestId == row.Id && r.Outcome == RecipientOutcome.Pending).ToList())
            {
                (member.Outcome, member.NextAt) = (RecipientOutcome.Failed, null);
            }
            logger.LogError("Digest {Digest} in tenant {Tenant} was given up, its members not sent: {Error}", row.Id, row.TenantId, error);
            context.Done(row, null);
        }

        public async Task ExecuteAsync(StageContext context, Digest row, CancellationToken ct)
        {
            var db = context.Db;
            var now = context.Now;
            if (row.State == DigestState.Cancelled)
            {
                context.Done(row, null);
                return;
            }
            if (row.State == DigestState.Open)
            {
                await CloseAsync(db, row, now, ct);
            }
            var members = await db.Recipients.Where(r => r.TenantId == row.TenantId && r.DigestId == row.Id && r.Outcome == RecipientOutcome.Pending)
                .OrderBy(r => r.Id).ToListAsync(ct);
            if (members.Count == 0)
            {
                // Everyone left (a resolved correlation) or was planned by an earlier try.
                if (!await db.Recipients.AsNoTracking().AnyAsync(r => r.TenantId == row.TenantId && r.DigestId == row.Id && r.Outcome != RecipientOutcome.Cancelled, ct))
                {
                    row.State = DigestState.Cancelled;
                }
                context.Done(row, null);
                return;
            }
            var notificationIds = members.Select(m => m.NotificationId).Distinct().ToList();
            var requests = (await (from n in db.Notifications
                                   join r in db.Requests on new { n.TenantId, Id = n.RequestId } equals new { r.TenantId, r.Id }
                                   where n.TenantId == row.TenantId && notificationIds.Contains(n.Id)
                                   select new { NotificationId = n.Id, Request = r }).AsNoTracking().ToListAsync(ct))
                .ToDictionary(x => x.NotificationId, x => x.Request);
            var typeIds = requests.Values.Select(r => r.TypeId).Distinct().ToList();
            var types = await db.Types.AsNoTracking().Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

            // As at a send: the tenant's mutes and the people's opt-outs now. An expired request's member is cancelled.
            foreach (var byRequest in members.GroupBy(m => m.NotificationId))
            {
                var request = requests[byRequest.Key];
                var list = byRequest.ToList();
                if (request.ExpiresAt is { } expires && expires <= now)
                {
                    list.ForEach(m => (m.Outcome, m.NextAt) = (RecipientOutcome.Cancelled, null));
                    continue;
                }
                list.ForEach(m => (m.Outcome, m.NextAt) = (RecipientOutcome.Planned, now));
                await People.PreferencePolicy.ApplyAsync(db, request, types[request.TypeId], list, mutes, now, ct);
            }

            // Each member's destinations, as the delivery planner reaches them.
            var reach = new List<(NotificationRecipient Member, DeliveryPlannerStage.Destination Destination, DateTimeOffset? Until)>();
            var allOff = new HashSet<Guid>();
            // A route's members of one type reach the same targets: looked up once per route and type, not once per member (a storm digest can
            // carry a thousand of them).
            var byRoute = new Dictionary<(Guid Route, Guid Type), List<DeliveryPlannerStage.Destination>>();
            foreach (var member in members.Where(m => m.Outcome == RecipientOutcome.Planned))
            {
                var type = types[requests[member.NotificationId].TypeId];
                List<DeliveryPlannerStage.Destination> destinations;
                if (member is { Kind: RecipientKind.Route, RouteId: { } routeId })
                {
                    if (!byRoute.TryGetValue((routeId, type.Id), out destinations!))
                    {
                        byRoute[(routeId, type.Id)] = destinations = await planner.DestinationsAsync(db, member, type, ct);
                    }
                }
                else
                {
                    destinations = await planner.DestinationsAsync(db, member, type, ct);
                }
                DateTimeOffset? until = null;
                if (member.Kind == RecipientKind.Subscriber && member.SubscriberId is { } person)
                {
                    (destinations, var off, until) = await DeliveryPlannerStage.PersonalAsync(db, member, person, type, destinations, now, ct);
                    if (off)
                    {
                        allOff.Add(member.Id);
                    }
                }
                foreach (var destination in destinations)
                {
                    if (destination.Integration is null)
                    {
                        logger.LogWarning("Digest {Digest} member {Recipient} can't reach {Destination}: {Problem}", row.Id, member.Id, destination.Key, destination.Problem);
                        continue;
                    }
                    reach.Add((member, destination, destination.Channel == Channels.Inbox ? null : until));
                }
            }
            var memberIds = members.Select(m => m.Id).ToList();
            var existing = (await db.DeliveryItems.AsNoTracking().Where(i => i.TenantId == row.TenantId && memberIds.Contains(i.RecipientId))
                .Select(i => new { i.RecipientId, i.DestinationKey, i.Channel }).ToListAsync(ct)).Select(i => (i.RecipientId, i.DestinationKey, i.Channel)).ToHashSet();
            reach.RemoveAll(r => existing.Contains((r.Member.Id, r.Destination.Key, r.Destination.Channel)));

            var reached = new HashSet<Guid>(existing.Select(e => e.RecipientId));
            var created = 0;
            // The inbox: an item per notification. Everything else: one delivery per destination and channel for all its members.
            var rendered = new Dictionary<(string Channel, string Carried, RenderEscalation? Escalation), DeliveryPlannerStage.Rendered>();
            var groups = reach.GroupBy(r => r.Destination.Channel == Channels.Inbox ? (r.Destination.Key, r.Destination.Channel, r.Member.Id) : (r.Destination.Key, r.Destination.Channel, Guid.Empty));
            foreach (var group in groups)
            {
                var entries = group.OrderBy(e => requests[e.Member.NotificationId].AcceptedAt).ThenBy(e => e.Member.Id).ToList();
                var destination = entries[0].Destination;
                var carried = entries.Select(e => requests[e.Member.NotificationId]).ToList();
                var first = carried[0];
                var type = types[first.TypeId];
                var isDigest = destination.Channel != Channels.Inbox;
                // Its id before rendering, and the first member's escalation step, as a single notification's delivery has (M46-0).
                var id = Guid.CreateVersion7();
                var model = new RenderModel(type.Key, type.Version, type.Name, first.Priority, JsonElement.Parse(first.Data), first.Labels, first.Links,
                    isDigest ? carried.Take(DigestRules.MaxListed).Select(r => new DigestItem(JsonElement.Parse(r.Data), r.Labels, r.Links, r.Priority, r.AcceptedAt,
                        r.CorrelationKey)).ToList() : null,
                    isDigest ? Math.Max(0, carried.Count - DigestRules.MaxListed) : 0,
                    new RenderDelivery(id), await DeliveryPlannerStage.EscalationOfAsync(db, entries[0].Member, ct));
                // Once for every destination of a channel carrying the same notifications, unless the template reads delivery (M46-0).
                var renderKey = (destination.Channel, string.Join(",", carried.Select(r => r.Id)), model.Escalation);
                if (!rendered.TryGetValue(renderKey, out var once))
                {
                    once = await planner.RenderAsync(db, row.TenantId, type, first, destination.Channel,
                        channels.Find(destination.Channel), model, timeoutIsFinal: row.StepFailures >= 2, ct, isDigest ? carried : null);
                    if (!DeliveryPlannerStage.PerDelivery(once))
                    {
                        rendered[renderKey] = once;
                    }
                }
                var (content, template, problem, category) = once;
                if (problem is null && destination.Problem is not null)
                {
                    (problem, category) = (destination.Problem, FailureCategory.Provider);
                }
                var until = entries.Select(e => e.Until).FirstOrDefault(u => u is not null);
                var delivery = new Delivery
                {
                    Id = id, TenantId = row.TenantId, DigestId = isDigest ? row.Id : null, Channel = destination.Channel, IntegrationId = destination.Integration!.Id,
                    DestinationKey = destination.Key, DestinationSnapshot = destination.Snapshot, TemplateVersion = template?.Version ?? 0,
                    Locale = template?.Locale ?? Types.TypeRules.DefaultLocale, RendererVersion = TemplateRenderer.Version, Content = content,
                    Priority = carried.Max(r => r.Priority), Mandatory = type.Mandatory, CreatedAt = now, DryRun = DeliveryPlannerStage.DryRun(context),
                    Status = problem is null ? DeliveryStatus.Pending : DeliveryStatus.Failed, FailureCategory = problem is null ? null : category,
                    FailureCheckDue = problem is not null,
                    LastError = ChannelContent.Cut(problem, 2000), NextAt = problem is null ? until ?? now : null, CompletedAt = problem is null ? null : now,
                };
                db.Deliveries.Add(delivery);
                foreach (var entry in entries)
                {
                    db.DeliveryItems.Add(new DeliveryItem
                    {
                        TenantId = row.TenantId, DeliveryId = delivery.Id, RecipientId = entry.Member.Id, RequestId = requests[entry.Member.NotificationId].Id,
                        DestinationKey = destination.Key, Channel = destination.Channel,
                    });
                    reached.Add(entry.Member.Id);
                }
                created++;
            }
            var planned = 0;
            foreach (var member in members.Where(m => m.Outcome == RecipientOutcome.Planned))
            {
                member.NextAt = null;
                if (reached.Contains(member.Id))
                {
                    planned++;
                }
                else
                {
                    member.Outcome = allOff.Contains(member.Id) ? RecipientOutcome.OptedOut : RecipientOutcome.Failed;
                }
            }
            row.Members += members.Count;
            logger.LogDebug("Digest {Digest} ({Type}, {By} {Key}) closed: {Members} members, {Planned} planned, {Deliveries} deliveries",
                row.Id, row.TypeKey, row.By, row.Key, members.Count, planned, created);
            var outcomes = members.GroupBy(m => m.Outcome.ToString()).Select(g => (g.Key, g.Count())).ToList();
            context.AfterCommit.Add(() =>
            {
                foreach (var (outcome, count) in outcomes)
                {
                    Telemetry.Recipients.Add(count, new KeyValuePair<string, object?>("outcome", outcome));
                }
            });
            context.Done(row, null);
            if (created > 0)
            {
                context.Wakes.Add(DeliveryStageNames.Dispatcher);
            }
        }
    }
}
