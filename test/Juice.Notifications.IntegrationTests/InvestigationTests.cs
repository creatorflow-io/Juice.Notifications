using System.Net;
using System.Net.Http.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>The Console's reads (phase-4c-plan I52) over seeded rows, each in a tenant of its own (the platform admin administers any).</summary>
    public sealed class InvestigationApp : IAsyncLifetime
    {
        public NotificationsApp App { get; } = new();

        public ValueTask InitializeAsync() => App.InitializeAsync();

        public ValueTask DisposeAsync() => App.DisposeAsync();

        /// <summary>A new tenant with an application linked to it and a type: what a notification needs.</summary>
        public async Task<(Guid Tenant, Application Application, NotificationType Type)> TenantAsync()
        {
            var tenant = Guid.CreateVersion7();
            var application = Seed.Application("inv" + Guid.NewGuid().ToString("N")[..8]);
            var type = Seed.Type(application.Id, "inv.thing");
            await using var db = App.Database.Context();
            db.AddRange(application, new ApplicationTenant { ApplicationId = application.Id, TenantId = tenant, LinkedAt = Seed.Now }, type);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return (tenant, application, type);
        }

        /// <summary>A request and its notification in <paramref name="state"/>.</summary>
        public static (NotificationRequest Request, Notification Notification) Notification(Guid tenant, Application application, NotificationType type,
            NotificationState state = NotificationState.Planned, string? correlation = null, Guid? correlationId = null, string typeKey = "inv.thing")
        {
            var request = new NotificationRequest
            {
                TenantId = tenant, ApplicationId = application.Id, IdempotencyKey = Guid.NewGuid().ToString("N"), TypeId = type.Id, TypeKey = typeKey, TypeVersion = 1,
                SchemaHash = "h", Data = "{}", Recipients = "[]", AcceptedAt = DateTimeOffset.UtcNow, CorrelationKey = correlation, CorrelationId = correlationId,
                Actor = "alice",
            };
            var notification = Seed.Notification(tenant, request.Id);
            notification.State = state;
            notification.NextAt = null;
            return (request, notification);
        }
    }

    /// <summary>
    /// "Why didn't they get it?" (phase-4c-plan proof point 3, deferred #36): a notification's recipients each say what came of them, the ones
    /// held before any delivery included.
    /// </summary>
    public sealed class NotificationRecipientsTests(InvestigationApp fixture) : IClassFixture<InvestigationApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task A_muted_opted_out_and_deferred_recipient_each_shows_why_with_no_delivery()
        {
            LocalStack.RequireAvailable();
            var (tenant, application, type) = await fixture.TenantAsync();
            var (request, notification) = InvestigationApp.Notification(tenant, application, type);
            var people = Enumerable.Range(0, 5).Select(i => new Subscriber { TenantId = tenant, Source = "local", ExternalId = $"p{i}", CreatedAt = Seed.Now }).ToArray();
            NotificationRecipient Person(Subscriber s, RecipientOutcome outcome, params string[] bypassed) => new()
            {
                TenantId = tenant, NotificationId = notification.Id, Kind = RecipientKind.Subscriber, SubscriberId = s.Id, Via = ["group:sre", $"user:{s.ExternalId}"],
                Resolver = "local", NamedAs = $"subscriber:{s.Id}", Outcome = outcome, Bypassed = bypassed, CreatedAt = Seed.Now,
            };
            var muted = Person(people[0], RecipientOutcome.Muted);
            var optedOut = Person(people[1], RecipientOutcome.OptedOut);
            var deferred = Person(people[2], RecipientOutcome.Deferred);
            var sent = Person(people[3], RecipientOutcome.Planned, BypassedRules.QuietHours);
            var digest = new Digest { TenantId = tenant, TypeKey = "inv.thing", By = "subscriber", Key = "k", State = DigestState.Open, OpensAt = Seed.Now,
                ClosesAt = Seed.Now.AddHours(1) };
            var waiting = Person(people[4], RecipientOutcome.Planned);
            waiting.DigestId = digest.Id;
            var integration = Seed.Integration(tenant);
            var delivery = Seed.Delivery(tenant, integration.Id);
            delivery.Status = DeliveryStatus.Delivered;
            await using (var db = fixture.App.Database.Context())
            {
                db.AddRange(people);
                db.AddRange(request, notification, digest, integration, muted, optedOut, deferred, sent, waiting, delivery,
                    Seed.Item(tenant, delivery.Id, sent.Id, request.Id));
                await db.SaveChangesAsync(Ct);
            }
            using var admin = fixture.App.Client(await LocalStack.AdminTokenAsync());

            var page = await admin.GetFromJsonAsync<RecipientPageResponse>($"/api/v1/tenants/{tenant}/notifications/{request.Id}/recipients", Ct);
            var byId = page!.Items.ToDictionary(r => r.Id);
            Assert.Null(page.Next);
            Assert.Equal(5, byId.Count);
            foreach (var (held, why) in new[] { (muted, RecipientOutcome.Muted), (optedOut, RecipientOutcome.OptedOut), (deferred, RecipientOutcome.Deferred) })
            {
                Assert.Equal(why, byId[held.Id].Outcome);
                Assert.Empty(byId[held.Id].Deliveries);
                Assert.Equal(["group:sre", $"user:{people.Single(p => p.Id == held.SubscriberId).ExternalId}"], byId[held.Id].Via);
            }
            // The one sent went past quiet hours (a mandatory type), with its delivery.
            Assert.Equal([BypassedRules.QuietHours], byId[sent.Id].Bypassed);
            Assert.Equal((delivery.Id, DeliveryStatus.Delivered), (Assert.Single(byId[sent.Id].Deliveries).Id, byId[sent.Id].Deliveries[0].Status));
            // The one in a digest: no delivery yet, and the digest it waits for.
            Assert.Empty(byId[waiting.Id].Deliveries);
            Assert.Equal((digest.Id, DigestState.Open), (byId[waiting.Id].Digest!.Id, byId[waiting.Id].Digest!.State));

            // Paged oldest first, by the after cursor.
            var first = await admin.GetFromJsonAsync<RecipientPageResponse>($"/api/v1/tenants/{tenant}/notifications/{request.Id}/recipients?limit=2", Ct);
            var rest = await admin.GetFromJsonAsync<RecipientPageResponse>(
                $"/api/v1/tenants/{tenant}/notifications/{request.Id}/recipients?limit=200&after={first!.Next}", Ct);
            Assert.Equal(page.Items.Select(r => r.Id), first.Items.Concat(rest!.Items).Select(r => r.Id));
            Assert.Equal(page.Items.Select(r => r.Id).Order(), page.Items.Select(r => r.Id));
        }

        [Fact]
        public async Task Recipients_are_for_the_tenants_admins_only_and_another_tenants_notification_is_not_found()
        {
            LocalStack.RequireAvailable();
            var (tenant, application, type) = await fixture.TenantAsync();
            var (request, notification) = InvestigationApp.Notification(tenant, application, type);
            await using (var db = fixture.App.Database.Context())
            {
                db.AddRange(request, notification);
                await db.SaveChangesAsync(Ct);
            }
            // The tests' tenant admin administers another tenant: refused here, as any path of this tenant is.
            using var tenantAdmin = fixture.App.Client(await LocalStack.TenantAdminTokenAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync($"/api/v1/tenants/{tenant}/notifications/{request.Id}/recipients", Ct)).StatusCode);
            // And in their own tenant, this notification isn't there.
            Assert.Equal(HttpStatusCode.NotFound,
                (await tenantAdmin.GetAsync($"/api/v1/tenants/{ProducerApp.Tenant}/notifications/{request.Id}/recipients", Ct)).StatusCode);
            using var producer = fixture.App.Client(await LocalStack.ProducerTokenAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await producer.GetAsync($"/api/v1/tenants/{tenant}/notifications/{request.Id}/recipients", Ct)).StatusCode);
            using var admin = fixture.App.Client(await LocalStack.AdminTokenAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/v1/tenants/{tenant}/notifications/{request.Id}/recipients?limit=0", Ct)).StatusCode);
        }
    }

    /// <summary>The notifications list, a notification for its admins, the correlations list with an admin's actions, and the overview.</summary>
    public sealed class InvestigationTests(InvestigationApp fixture) : IClassFixture<InvestigationApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task The_notifications_list_filters_and_pages_newest_first()
        {
            LocalStack.RequireAvailable();
            var (tenant, application, type) = await fixture.TenantAsync();
            // Made a few milliseconds apart: version-7 ids made in the same millisecond have no order between them.
            var made = new List<(NotificationRequest Request, Notification Notification)>();
            foreach (var (state, correlation, typeKey) in new[] { (NotificationState.Planned, (string?)null, "inv.thing"),
                (NotificationState.Running, "order-1", "inv.thing"), (NotificationState.Failed, null, "inv.other") })
            {
                made.Add(InvestigationApp.Notification(tenant, application, type, state, correlation, typeKey: typeKey));
                await Task.Delay(5, Ct);
            }
            made[2].Notification.Error = "the type is gone";
            await using (var db = fixture.App.Database.Context())
            {
                foreach (var (request, notification) in made)
                {
                    db.AddRange(request, notification);
                    await db.SaveChangesAsync(Ct); // in order: ids are version 7
                }
            }
            using var admin = fixture.App.Client(await LocalStack.AdminTokenAsync());
            var url = $"/api/v1/tenants/{tenant}/notifications";

            var all = await admin.GetFromJsonAsync<NotificationPageResponse>(url, Ct);
            Assert.Equal(made.Select(m => m.Request.Id).Reverse(), all!.Items.Select(n => n.Id));
            Assert.All(all.Items, n => Assert.Equal(application.Key, n.Application));
            Assert.Equal("the type is gone", all.Items[0].Error);

            Assert.Equal([made[1].Request.Id], (await admin.GetFromJsonAsync<NotificationPageResponse>($"{url}?state=running", Ct))!.Items.Select(n => n.Id));
            Assert.Equal([made[1].Request.Id], (await admin.GetFromJsonAsync<NotificationPageResponse>($"{url}?correlation=order-1", Ct))!.Items.Select(n => n.Id));
            Assert.Equal([made[2].Request.Id], (await admin.GetFromJsonAsync<NotificationPageResponse>($"{url}?type=inv.other", Ct))!.Items.Select(n => n.Id));
            Assert.Empty((await admin.GetFromJsonAsync<NotificationPageResponse>($"{url}?application=nope", Ct))!.Items);

            var first = await admin.GetFromJsonAsync<NotificationPageResponse>($"{url}?limit=2", Ct);
            Assert.Equal(2, first!.Items.Count);
            var second = await admin.GetFromJsonAsync<NotificationPageResponse>($"{url}?limit=2&before={first.Next}", Ct);
            Assert.Equal([made[0].Request.Id], second!.Items.Select(n => n.Id));
            Assert.Null(second.Next);

            var bad = await admin.GetAsync($"{url}?state=1", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("/state", await bad.Content.ReadAsStringAsync(Ct));

            // One notification, for the admin: any application's, naming it.
            var one = await admin.GetFromJsonAsync<NotificationResponse>($"{url}/{made[1].Request.Id}", Ct);
            Assert.Equal((made[1].Request.Id, NotificationState.Running, application.Key), (one!.Id, one.State, one.Application));

            // Not a producer's list: it reads its own notifications by id.
            using var producer = fixture.App.Client(await LocalStack.ProducerTokenAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await producer.GetAsync(url, Ct)).StatusCode);
        }

        [Fact]
        public async Task The_correlations_list_names_the_application_and_an_admin_acts_as_themselves()
        {
            LocalStack.RequireAvailable();
            var (tenant, application, type) = await fixture.TenantAsync();
            var open = new Correlation { TenantId = tenant, ApplicationId = application.Id, Key = "order/1", State = CorrelationState.Open, CreatedAt = Seed.Now,
                LastRequestAt = Seed.Now, UpdatedAt = Seed.Now };
            await Task.Delay(5, Ct); // a later id
            var resolved = new Correlation { TenantId = tenant, ApplicationId = application.Id, Key = "order-2", State = CorrelationState.Resolved, CreatedAt = Seed.Now,
                LastRequestAt = Seed.Now, UpdatedAt = Seed.Now, ResolvedAt = Seed.Now, Outcome = "shipped" };
            await using (var db = fixture.App.Database.Context())
            {
                db.Add(open);
                await db.SaveChangesAsync(Ct);
                db.Add(resolved);
                var (request, notification) = InvestigationApp.Notification(tenant, application, type, correlation: open.Key, correlationId: open.Id);
                db.AddRange(request, notification);
                await db.SaveChangesAsync(Ct);
            }
            using var admin = fixture.App.Client(await LocalStack.AdminTokenAsync());
            var url = $"/api/v1/tenants/{tenant}/correlations";

            var all = await admin.GetFromJsonAsync<CorrelationPageResponse>(url, Ct);
            Assert.Equal([resolved.Key, open.Key], all!.Items.Select(c => c.Key));
            Assert.All(all.Items, c => Assert.Equal(application.Key, c.Application));
            Assert.Equal("shipped", all.Items[0].Outcome);
            Assert.Equal([open.Key], (await admin.GetFromJsonAsync<CorrelationPageResponse>($"{url}?state=open", Ct))!.Items.Select(c => c.Key));

            // One, by its application and key (a slash in it encoded), with its notifications.
            var key = Uri.EscapeDataString(open.Key);
            var one = await admin.GetFromJsonAsync<CorrelationResponse>($"{url}/{key}?application={application.Key}", Ct);
            Assert.Equal((open.Key, CorrelationState.Open), (one!.Key, one.State));
            Assert.Single(one.Notifications);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{url}/{key}?application=nope", Ct)).StatusCode);

            // The admin acknowledges as themselves, whatever the body names.
            var acknowledged = await admin.PostAsJsonAsync($"{url}/{key}/acknowledge?application={application.Key}", new AcknowledgeCorrelationBody("someone-else"), Ct);
            Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
            var after = await acknowledged.Content.ReadFromJsonAsync<CorrelationResponse>(Ct);
            Assert.Equal(CorrelationState.Acknowledged, after!.State);
            Assert.Equal("client:notifications-admin", after.AcknowledgedBy);

            // A tenant admin of another tenant may not name an application here; a producer reads its own correlations, not the list.
            using var tenantAdmin = fixture.App.Client(await LocalStack.TenantAdminTokenAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync($"{url}/{key}?application={application.Key}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await tenantAdmin.PostAsJsonAsync($"{url}/{key}/cancel?application={application.Key}", new CancelCorrelationBody(null), Ct)).StatusCode);
            using var producer = fixture.App.Client(await LocalStack.ProducerTokenAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await producer.GetAsync(url, Ct)).StatusCode);
        }

        [Fact]
        public async Task The_overview_counts_the_last_day_and_is_kept_for_30_seconds()
        {
            LocalStack.RequireAvailable();
            var (tenant, application, type) = await fixture.TenantAsync();
            var now = DateTimeOffset.UtcNow;
            var hooks = Seed.Integration(tenant, "hooks");
            var mail = Seed.Integration(tenant, "mail");
            Delivery Made(Integration integration, DeliveryStatus status, TimeSpan ago, string? error = null)
            {
                var d = Seed.Delivery(tenant, integration.Id);
                return new Delivery
                {
                    TenantId = tenant, Channel = integration.Key == "mail" ? Channels.Email : Channels.Webhook, IntegrationId = integration.Id,
                    DestinationKey = d.DestinationKey, DestinationSnapshot = d.DestinationSnapshot, Locale = "en", RendererVersion = "test", Content = "{}",
                    Status = status, LastError = error, CreatedAt = now - ago, CompletedAt = now - ago,
                };
            }
            var (unfinishedRequest, unfinished) = InvestigationApp.Notification(tenant, application, type, NotificationState.Running);
            var (doneRequest, done) = InvestigationApp.Notification(tenant, application, type, NotificationState.Planned);
            await using (var db = fixture.App.Database.Context())
            {
                db.AddRange(hooks, mail);
                await db.SaveChangesAsync(Ct);
                db.AddRange(Made(hooks, DeliveryStatus.Delivered, TimeSpan.FromHours(1)), Made(hooks, DeliveryStatus.Delivered, TimeSpan.FromHours(2)),
                    Made(hooks, DeliveryStatus.Failed, TimeSpan.FromHours(3), "older"));
                await db.SaveChangesAsync(Ct);
                db.AddRange(Made(hooks, DeliveryStatus.Failed, TimeSpan.FromMinutes(5), "503 from the receiver"), Made(mail, DeliveryStatus.Failed, TimeSpan.FromHours(1), "bounced"),
                    // Outside the window: not counted.
                    Made(mail, DeliveryStatus.Failed, TimeSpan.FromHours(30), "long ago"),
                    new Storm { TenantId = tenant, RouteKey = "ops", StartedAt = now, LastHighAt = now, Peak = 10 },
                    new Storm { TenantId = tenant, RouteKey = "old", StartedAt = now.AddDays(-1), EndedAt = now.AddHours(-20), LastHighAt = now.AddDays(-1) },
                    unfinishedRequest, unfinished, doneRequest, done);
                await db.SaveChangesAsync(Ct);
            }
            using var admin = fixture.App.Client(await LocalStack.AdminTokenAsync());
            var url = $"/api/v1/tenants/{tenant}/overview";

            var overview = await admin.GetFromJsonAsync<OverviewResponse>(url, Ct);
            Assert.Equal([(Channels.Email, DeliveryStatus.Failed, 1), (Channels.Webhook, DeliveryStatus.Delivered, 2), (Channels.Webhook, DeliveryStatus.Failed, 2)],
                overview!.Deliveries.Select(d => (d.Channel, d.Status, d.Count)));
            Assert.Equal([("hooks", 2, "503 from the receiver"), ("mail", 1, "bounced")],
                overview.FailingIntegrations.Select(i => (i.Integration, i.Failed, i.LastError)));
            Assert.Equal((1, 1), (overview.OpenStorms, overview.Unfinished));
            Assert.Equal(overview.At - TimeSpan.FromHours(24), overview.Since);

            // Kept: a change within 30 seconds isn't seen yet.
            await using (var db = fixture.App.Database.Context())
            {
                db.Add(new Storm { TenantId = tenant, RouteKey = "new", StartedAt = now, LastHighAt = now });
                await db.SaveChangesAsync(Ct);
            }
            var again = await admin.GetFromJsonAsync<OverviewResponse>(url, Ct);
            Assert.Equal((overview.At, 1), (again!.At, again.OpenStorms));

            using var tenantAdmin = fixture.App.Client(await LocalStack.TenantAdminTokenAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync(url, Ct)).StatusCode);
        }
    }
}
