using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.People;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Contact points and device registrations (phase-2-plan M13) in the real host: a person adds an address and verifies it with a code sent by
    /// email through the pipeline, only verified and working addresses are ever planned (phase 2 proof point 2), and devices register, move and
    /// are swept. The person is the local stack's tenant-admin client (deferred #27).
    /// </summary>
    public sealed class ContactPointTests(PeopleApp people) : IClassFixture<PeopleApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid TenantId = ProducerApp.Tenant;
        private static readonly string Tenant = $"/api/v1/tenants/{TenantId}";
        private static readonly string Points = $"{Tenant}/me/contact-points";

        private static JsonElement Json(string json) => JsonElement.Parse(json);

        private static string Mailbox(string name) => $"{name}-{Guid.NewGuid():N}"[..(name.Length + 11)] + "@example.com";

        private static async Task<T> OkAsync<T>(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
        }

        private static async Task<(HttpStatusCode Status, string Code)> ProblemAsync(Task<HttpResponseMessage> call)
        {
            var (status, code, _) = await DetailAsync(call);
            return (status, code);
        }

        private static async Task<(HttpStatusCode Status, string Code, string Detail)> DetailAsync(Task<HttpResponseMessage> call)
        {
            var response = await call;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            return (response.StatusCode, json.RootElement.GetProperty("code").GetString()!,
                json.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "" : "");
        }

        /// <summary>The tenant's email integration through the stack's Mailpit.</summary>
        private async Task MailIntegrationAsync()
        {
            using var admin = people.App.Client(await LocalStack.AdminTokenAsync());
            await OkAsync<JsonElement>(admin.PutAsJsonAsync($"{Tenant}/integrations/mail", new PutIntegrationBody("email", "smtp",
                Json("""{ "server": "local", "from": "notifications@juice.local" }"""), null, null, true), Ct));
        }

        /// <summary>The bodies of the messages Mailpit has for the address, once there are at least <paramref name="count"/>.</summary>
        private static async Task<List<string>> MailAsync(string address, int count = 1)
        {
            using var mailpit = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8026") };
            for (var i = 0; i < 150; i++)
            {
                var found = await mailpit.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={Uri.EscapeDataString("to:" + address)}", Ct);
                var ids = found.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("ID").GetString()!).ToList();
                if (ids.Count >= count)
                {
                    var bodies = new List<string>();
                    foreach (var id in ids)
                    {
                        bodies.Add((await mailpit.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}", Ct)).GetProperty("Text").GetString()!);
                    }
                    return bodies;
                }
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException($"No mail reached {address}.");
        }

        private static (HttpStatusCode, string) Short((HttpStatusCode Status, string Code, string Detail) problem) => (problem.Status, problem.Code);

        private static string CodeIn(string body) => System.Text.RegularExpressions.Regex.Match(body, @"\b(\d{6})\b").Groups[1].Value;

        [Fact]
        public async Task A_person_adds_an_address_gets_a_code_by_email_and_verifies_it()
        {
            LocalStack.RequireAvailable();
            await MailIntegrationAsync();
            using var me = people.App.Client(await LocalStack.PersonTokenAsync());
            var address = Mailbox("pat");

            var added = await OkAsync<ContactPointResponse>(me.PostAsJsonAsync(Points, new AddContactPointBody("email", address.ToUpperInvariant()), Ct));
            Assert.Equal((address, false, ContactHealth.Ok, (string?)null), (added.Address, added.Verified, added.Health, added.VerifiedBy));
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ContactPointExists),
                (await ProblemAsync(me.PostAsJsonAsync(Points, new AddContactPointBody("email", address), Ct))));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),
                (await ProblemAsync(me.PostAsJsonAsync(Points, new AddContactPointBody("webhook", "https://example.com"), Ct))));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),
                (await ProblemAsync(me.PostAsJsonAsync(Points, new AddContactPointBody("email", "Pat <pat@example.com>"), Ct))));

            var sent = await OkAsync<VerificationSentResponse>(me.PostAsync($"{Points}/{added.Id}/verification", null, Ct));
            Assert.InRange(sent.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
            // Once a minute per address.
            Assert.Equal((HttpStatusCode.TooManyRequests, ErrorCodes.TooManyRequests),
                (await ProblemAsync(me.PostAsync($"{Points}/{added.Id}/verification", null, Ct))));
            var code = CodeIn(Assert.Single(await MailAsync(address)));
            Assert.Matches("^[0-9]{6}$", code);

            var wrong = ((int.Parse(code) + 1) % 1_000_000).ToString("D6");
            var refused = await DetailAsync(me.PostAsJsonAsync($"{Points}/{added.Id}/verify", new VerifyContactPointBody(wrong), Ct));
            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.VerificationFailed), Short(refused));
            Assert.Contains("4 tries left", refused.Detail);
            var verified = await OkAsync<ContactPointResponse>(me.PostAsJsonAsync($"{Points}/{added.Id}/verify", new VerifyContactPointBody(code), Ct));
            Assert.Equal((true, "self"), (verified.Verified, verified.VerifiedBy));
            // A code works once; there is nothing more to verify.
            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.VerificationExpired),
                (await ProblemAsync(me.PostAsJsonAsync($"{Points}/{added.Id}/verify", new VerifyContactPointBody(code), Ct))));
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.AlreadyVerified),
                (await ProblemAsync(me.PostAsync($"{Points}/{added.Id}/verification", null, Ct))));

            var listed = await OkAsync<List<ContactPointResponse>>(me.GetAsync(Points, Ct));
            Assert.Contains(listed, p => p.Id == added.Id && p.Verified && p.Address == address);

            // The code went out as the service's own type, naming the contact point, like any notification. The address is nowhere but the
            // contact point, encrypted: not in the request, its recipient, the delivery's key or its history.
            await using (var db = people.App.Database.Context())
            {
                var request = (await db.Requests.AsNoTracking().Where(r => r.TypeKey == ServiceTypes.VerifyType).ToListAsync(Ct))
                    .Single(r => r.Recipients.Contains(added.Id.ToString(), StringComparison.Ordinal));
                Assert.DoesNotContain(address, request.Recipients);
                var app = await db.Applications.AsNoTracking().SingleAsync(a => a.Id == request.ApplicationId, Ct);
                Assert.Equal(ServiceTypes.ApplicationKey, app.Key);
                var recipient = await db.Recipients.AsNoTracking().SingleAsync(r => db.Notifications.Any(n => n.Id == r.NotificationId && n.RequestId == request.Id), Ct);
                Assert.Equal((RecipientKind.ContactPoint, (string?)null, $"contact-point:{added.Id}"), (recipient.Kind, recipient.Address, recipient.NamedAs));
                var delivery = await db.Deliveries.AsNoTracking().SingleAsync(d => db.DeliveryItems.Any(i => i.DeliveryId == d.Id && i.RequestId == request.Id), Ct);
                Assert.Equal($"cp:{added.Id}", delivery.DestinationKey);
                Assert.DoesNotContain(address, delivery.DestinationSnapshot);
                var stored = await db.ContactPoints.AsNoTracking().SingleAsync(c => c.Id == added.Id, Ct);
                Assert.DoesNotContain("example.com", stored.Address);
                Assert.Equal(64, stored.AddressHash.Length);
            }

            Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync($"{Points}/{added.Id}", Ct)).StatusCode);
            Assert.DoesNotContain(await OkAsync<List<ContactPointResponse>>(me.GetAsync(Points, Ct)), p => p.Id == added.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await me.DeleteAsync($"{Points}/{added.Id}", Ct)).StatusCode);
        }

        /// <summary>A code expires after its 15 minutes, and stops working after five tries even when the sixth is right.</summary>
        [Fact]
        public async Task A_code_expires_and_runs_out_of_tries()
        {
            LocalStack.RequireAvailable();
            await MailIntegrationAsync();
            using var me = people.App.Client(await LocalStack.PersonTokenAsync());
            var added = await OkAsync<ContactPointResponse>(me.PostAsJsonAsync(Points, new AddContactPointBody("email", Mailbox("tries")), Ct));
            await OkAsync<VerificationSentResponse>(me.PostAsync($"{Points}/{added.Id}/verification", null, Ct));
            // A known code in place of the one sent, as the service would have hashed it.
            var crypto = people.App.Services.GetRequiredService<ContactCrypto>();
            var known = crypto.Hash("verify", TenantId.ToString(), added.Id.ToString(), "123456");
            async Task SetAsync(DateTimeOffset expires)
            {
                await using var db = people.App.Database.Context();
                await db.ContactVerifications.Where(v => v.ContactPointId == added.Id)
                    .ExecuteUpdateAsync(v => v.SetProperty(x => x.CodeHash, known).SetProperty(x => x.ExpiresAt, expires).SetProperty(x => x.Tries, 0), Ct);
            }
            Task<(HttpStatusCode Status, string Code, string Detail)> Verify(string code)
                => DetailAsync(me.PostAsJsonAsync($"{Points}/{added.Id}/verify", new VerifyContactPointBody(code), Ct));

            await SetAsync(DateTimeOffset.UtcNow.AddSeconds(-1));
            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.VerificationExpired), Short(await Verify("123456")));

            await SetAsync(DateTimeOffset.UtcNow.AddMinutes(10));
            for (var left = 4; left >= 0; left--)
            {
                var refused = await Verify("654321");
                Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.VerificationFailed), Short(refused));
                Assert.Contains(left == 0 ? "last try" : $"{left} tr", refused.Detail);
            }
            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.VerificationExpired), Short(await Verify("123456")));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest), Short(await Verify("12345")));
            Assert.False((await OkAsync<List<ContactPointResponse>>(me.GetAsync(Points, Ct))).Single(p => p.Id == added.Id).Verified);
        }

        /// <summary>A tenant that can't send email gets no code sent, and nothing counted against the person.</summary>
        [Fact]
        public async Task No_code_is_sent_where_the_tenant_cant_send_it()
        {
            LocalStack.RequireAvailable();
            using var me = people.App.Client(await LocalStack.PersonTokenAsync());
            var other = $"/api/v1/tenants/{ProducerApp.OtherTenant}/me/contact-points";
            // The tenant-admin client belongs to one tenant only.
            Assert.Equal(HttpStatusCode.Forbidden, (await me.GetAsync(other, Ct)).StatusCode);

            using var admin = people.App.Client(await LocalStack.AdminTokenAsync());
            await OkAsync<JsonElement>(admin.PutAsJsonAsync($"{Tenant}/integrations/mail", new PutIntegrationBody("email", "smtp",
                Json("""{ "server": "local", "from": "notifications@juice.local" }"""), null, null, false), Ct));
            try
            {
                var added = await OkAsync<ContactPointResponse>(me.PostAsJsonAsync(Points, new AddContactPointBody("email", Mailbox("nomail")), Ct));
                Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.ChannelUnavailable),
                    (await ProblemAsync(me.PostAsync($"{Points}/{added.Id}/verification", null, Ct))));
            }
            finally
            {
                await MailIntegrationAsync();
            }
        }

        /// <summary>
        /// Phase 2 proof point 2: an unverified or failed contact point is never planned, even for a mandatory type. A source's verified address
        /// is used, its unverified one isn't, and a person with no usable address has no deliveries (and says so).
        /// </summary>
        [Fact]
        public async Task Unverified_or_failed_contact_points_are_never_planned_even_for_mandatory_types()
        {
            LocalStack.RequireAvailable();
            await MailIntegrationAsync();
            using var admin = people.App.Client(await LocalStack.AdminTokenAsync());
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            await OkAsync<ResolverResponse>(admin.PutAsJsonAsync($"{Tenant}/resolvers/crews", new PutResolverBody("fake", ["group"], null, null, null), Ct));
            var type = $"{ProducerApp.Key}.m{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            await OkAsync<JsonElement>(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types",
                new DeclareTypeBody(type, "Must know", "security", null, "high", true, null, Json("""{ "type": "object" }"""), null, null, null,
                    Json("""{ "email": "Subject: Must know\n\nRead this." }""")), Ct));

            var (annOk, annNew, benBad) = (Mailbox("ann-ok"), Mailbox("ann-new"), Mailbox("ben-bad"));
            var crew = $"crew-{Guid.NewGuid():N}";
            var ann = new ResolvedPerson("fake", "ann-" + crew, "Ann", Contacts: [new ResolvedContact("email", annOk, true), new ResolvedContact("email", annNew, false)]);
            var ben = new ResolvedPerson("fake", "ben-" + crew, "Ben", Contacts: [new ResolvedContact("email", benBad, true)]);
            people.People.Groups[crew] = [ann, ben];
            // The people and their sources' addresses, as a plan stores them; then Ben's address fails.
            var crypto = people.App.Services.GetRequiredService<ContactCrypto>();
            var channels = people.App.Services.GetRequiredService<IChannelRegistry>();
            await using (var db = people.App.Database.Context())
            {
                var map = await SubscriberDirectory.MapAsync(db, TenantId, [ann, ben], DateTimeOffset.UtcNow, new SourceContacts(crypto, channels, NullLogger.Instance), Ct);
                var benId = map[("fake", ben.ExternalId)];
                Assert.Equal(1, await db.ContactPoints.Where(c => c.SubscriberId == benId)
                    .ExecuteUpdateAsync(c => c.SetProperty(x => x.Health, ContactHealth.Failed).SetProperty(x => x.HealthReason, "5.1.1 user unknown"), Ct));
            }

            var accepted = await OkAsync<AcceptedResponse>(producer.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null,
                Guid.NewGuid().ToString("N"), null, [new RecipientBody("group", Id: crew, Resolver: "crews")], null, null, null, Json("{}"), null, null), Ct));

            await using var read = people.App.Database.Context();
            List<NotificationRecipient> recipients = [];
            for (var i = 0; i < 150; i++)
            {
                recipients = await read.Recipients.AsNoTracking().Where(r => read.Notifications.Any(n => n.Id == r.NotificationId && n.RequestId == accepted.Id))
                    .ToListAsync(Ct);
                if (recipients.Count == 2 && recipients.All(r => r.NextAt == null)
                    && await read.Deliveries.AsNoTracking().Where(d => read.DeliveryItems.Any(it => it.DeliveryId == d.Id && it.RequestId == accepted.Id))
                        .AllAsync(d => d.Status != DeliveryStatus.Pending && d.Status != DeliveryStatus.Sending && d.Status != DeliveryStatus.Retrying, Ct))
                {
                    break;
                }
                await Task.Delay(100, Ct);
            }
            var subscribers = await read.Subscribers.AsNoTracking().Where(s => s.Source == "fake" && (s.ExternalId == ann.ExternalId || s.ExternalId == ben.ExternalId))
                .ToDictionaryAsync(s => s.ExternalId, s => s.Id, Ct);
            Assert.Equal(RecipientOutcome.Planned, recipients.Single(r => r.SubscriberId == subscribers[ann.ExternalId]).Outcome);
            // Only his inbox reaches Ben: his only address failed, and mandatory doesn't bend that.
            Assert.Equal(RecipientOutcome.Planned, recipients.Single(r => r.SubscriberId == subscribers[ben.ExternalId]).Outcome);

            var point = await read.ContactPoints.AsNoTracking().SingleAsync(c => c.SubscriberId == subscribers[ann.ExternalId] && c.VerifiedAt != null, Ct);
            Assert.Equal("fake", point.VerifiedBy);
            var deliveries = await read.Deliveries.AsNoTracking()
                .Where(d => read.DeliveryItems.Any(it => it.DeliveryId == d.Id && it.RequestId == accepted.Id)).ToListAsync(Ct);
            // An inbox item each, and one email: Ann's verified address.
            Assert.Equal(2, deliveries.Count(d => d.Channel == Channels.Inbox));
            var delivery = Assert.Single(deliveries, d => d.Channel == Channels.Email);
            Assert.Equal(($"cp:{point.Id}", DeliveryStatus.Accepted, true), (delivery.DestinationKey, delivery.Status, delivery.Mandatory));
            // History shows the address masked, never the address.
            Assert.Contains("a…@example.com", delivery.DestinationSnapshot);
            Assert.DoesNotContain(annOk, delivery.DestinationSnapshot);
            Assert.Contains("Read this.", Assert.Single(await MailAsync(annOk)));
            using var mailpit = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8026") };
            foreach (var never in new[] { annNew, benBad })
            {
                var found = await mailpit.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={Uri.EscapeDataString("to:" + never)}", Ct);
                Assert.Empty(found.GetProperty("messages").EnumerateArray());
            }
        }

        /// <summary>
        /// A source's addresses: a new one arrives verified or not as the source says; the source can verify one later but never takes a
        /// verification back, and the person's own verification stays theirs.
        /// </summary>
        [Fact]
        public async Task A_source_vouches_for_an_address_but_never_takes_a_verification_back()
        {
            LocalStack.RequireAvailable();
            var crypto = people.App.Services.GetRequiredService<ContactCrypto>();
            var contacts = new SourceContacts(crypto, people.App.Services.GetRequiredService<IChannelRegistry>(), NullLogger.Instance);
            var (a, b) = (Mailbox("src-a"), Mailbox("src-b"));
            var id = $"src-{Guid.NewGuid():N}";
            Task<Dictionary<string, (string? By, bool Verified)>> PlanAsync(params ResolvedContact[] given) => PlanForAsync(id, given);
            async Task<Dictionary<string, (string? By, bool Verified)>> PlanForAsync(string person, params ResolvedContact[] given)
            {
                await using var db = people.App.Database.Context();
                var map = await SubscriberDirectory.MapAsync(db, TenantId, [new ResolvedPerson("fake", person, Contacts: given)], DateTimeOffset.UtcNow, contacts, Ct);
                var subscriberId = map[("fake", person)];
                var rows = await db.ContactPoints.AsNoTracking().Where(c => c.SubscriberId == subscriberId).ToListAsync(Ct);
                return rows.ToDictionary(c => crypto.Unprotect(c.Address, c.KeyId, ContactCrypto.ContactContext(c.TenantId, c.SubscriberId, c.Channel)),
                    c => (c.VerifiedBy, c.VerifiedAt != null));
            }

            var first = await PlanAsync(new ResolvedContact("email", a, false), new ResolvedContact("email", b.ToUpperInvariant(), true),
                new ResolvedContact("pigeon", "+15550100", true), new ResolvedContact("email", "not an address", true));
            Assert.Equal(new Dictionary<string, (string?, bool)> { [a] = (null, false), [b] = ("fake", true) }, first);

            var second = await PlanAsync(new ResolvedContact("email", a, true), new ResolvedContact("email", b, false));
            Assert.Equal(new Dictionary<string, (string?, bool)> { [a] = ("fake", true), [b] = ("fake", true) }, second);

            // A source can't grow a person's list past the limit, however many plans it takes.
            var many = Enumerable.Range(0, 25).Select(i => new ResolvedContact("email", Mailbox($"m{i:D2}"), false)).ToArray();
            var full = await PlanAsync(many);
            Assert.Equal(ContactRules.MaxPerPerson, full.Count);
            Assert.Equal(ContactRules.MaxPerPerson, (await PlanAsync(Enumerable.Range(0, 5).Select(i => new ResolvedContact("email", Mailbox($"n{i}"), true)).ToArray())).Count);
            // One it has already is still updated at the limit.
            var kept = full.First(p => !p.Value.Verified).Key;
            Assert.Equal(("fake", true), (await PlanAsync(new ResolvedContact("email", kept, true)))[kept]);

            // With room for one more, a new address goes in even beside ones the person has (they count once).
            var almost = $"src-{Guid.NewGuid():N}";
            var nineteen = Enumerable.Range(0, ContactRules.MaxPerPerson - 1).Select(i => new ResolvedContact("email", Mailbox($"r{i:D2}"), false)).ToArray();
            Assert.Equal(ContactRules.MaxPerPerson - 1, (await PlanForAsync(almost, nineteen)).Count);
            Assert.Equal(ContactRules.MaxPerPerson, (await PlanForAsync(almost, [.. nineteen, new ResolvedContact("email", Mailbox("last"), false)])).Count);
        }

        /// <summary>Storing a source's addresses failing (here, keys that can't be read) doesn't stop the people being mapped and planned.</summary>
        [Fact]
        public async Task A_source_address_that_cant_be_stored_doesnt_stop_planning()
        {
            LocalStack.RequireAvailable();
            var broken = new ContactCrypto(Microsoft.Extensions.Options.Options.Create(new EncryptionOptions { KeyRef = "env:NOTIFICATIONS_TEST_UNSET_KEY" }));
            var contacts = new SourceContacts(broken, people.App.Services.GetRequiredService<IChannelRegistry>(), NullLogger.Instance);
            var person = new ResolvedPerson("fake", $"iso-{Guid.NewGuid():N}", Contacts: [new ResolvedContact("email", Mailbox("iso"), true)]);
            await using var db = people.App.Database.Context();

            var map = await SubscriberDirectory.MapAsync(db, TenantId, [person], DateTimeOffset.UtcNow, contacts, Ct);

            var subscriberId = map[("fake", person.ExternalId)];
            Assert.False(await db.ContactPoints.AnyAsync(c => c.SubscriberId == subscriberId, Ct));
        }

        /// <summary>The same address on two identities of one person (linked by an admin) is sent to once.</summary>
        [Fact]
        public async Task One_address_on_two_linked_identities_is_one_delivery()
        {
            LocalStack.RequireAvailable();
            await MailIntegrationAsync();
            using var admin = people.App.Client(await LocalStack.AdminTokenAsync());
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            await OkAsync<ResolverResponse>(admin.PutAsJsonAsync($"{Tenant}/resolvers/crews", new PutResolverBody("fake", ["group"], null, null, null), Ct));
            var type = $"{ProducerApp.Key}.l{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            await OkAsync<JsonElement>(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types",
                new DeclareTypeBody(type, "Linked", "things", null, null, null, null, Json("""{ "type": "object" }"""), null, null, null,
                    Json("""{ "email": "Subject: Linked\n\nOnce." }""")), Ct));
            var shared = Mailbox("twice");
            var crew = $"crew-{Guid.NewGuid():N}";
            var main = new ResolvedPerson("fake", "main-" + crew, Contacts: [new ResolvedContact("email", shared, true)]);
            var other = new ResolvedPerson("fake", "other-" + crew, Contacts: [new ResolvedContact("email", shared, true)]);
            var crypto = people.App.Services.GetRequiredService<ContactCrypto>();
            await using (var db = people.App.Database.Context())
            {
                var map = await SubscriberDirectory.MapAsync(db, TenantId, [main, other], DateTimeOffset.UtcNow,
                    new SourceContacts(crypto, people.App.Services.GetRequiredService<IChannelRegistry>(), NullLogger.Instance), Ct);
                await OkAsync<SubscriberResponse>(admin.PostAsJsonAsync($"{Tenant}/subscribers/{map[("fake", main.ExternalId)]}/aliases",
                    new LinkAliasBody("fake", other.ExternalId), Ct));
            }
            people.People.Groups[crew] = [main, other];

            var accepted = await OkAsync<AcceptedResponse>(producer.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null,
                Guid.NewGuid().ToString("N"), null, [new RecipientBody("group", Id: crew, Resolver: "crews")], null, null, null, Json("{}"), null, null), Ct));

            Assert.Single(await MailAsync(shared));
            await Task.Delay(1000, Ct);
            Assert.Single(await MailAsync(shared));
            await using var read = people.App.Database.Context();
            // One email and one inbox item: the person, not each identity, is the recipient.
            Assert.Equal([Channels.Email, Channels.Inbox], (await read.Deliveries.AsNoTracking()
                .Where(d => read.DeliveryItems.Any(i => i.DeliveryId == d.Id && i.RequestId == accepted.Id)).Select(d => d.Channel).ToListAsync(Ct))
                .Order(StringComparer.Ordinal));
        }

        /// <summary>A producer can't name a contact point: only the service's own requests do.</summary>
        [Fact]
        public async Task A_producer_cant_name_a_contact_point()
        {
            LocalStack.RequireAvailable();
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            var type = $"{ProducerApp.Key}.c{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            await OkAsync<JsonElement>(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types",
                new DeclareTypeBody(type, "Direct", "things", null, null, null, true, Json("""{ "type": "object" }"""), null, null, null,
                    Json("""{ "email": "Hi" }""")), Ct));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest), await ProblemAsync(producer.PostAsJsonAsync($"{Tenant}/notifications",
                new SendNotificationBody(type, null, Guid.NewGuid().ToString("N"), null, [new RecipientBody("contact-point", Id: Guid.NewGuid().ToString())],
                    null, null, null, Json("{}"), null, null), Ct)));
        }

        [Fact]
        public async Task A_device_registers_moves_to_whoever_registers_it_last_and_is_swept_when_unseen()
        {
            LocalStack.RequireAvailable();
            using var me = people.App.Client(await LocalStack.PersonTokenAsync());
            var devices = $"{Tenant}/me/devices";
            var token = "fcm:" + Guid.NewGuid().ToString("N");

            var registered = await OkAsync<DeviceResponse>(me.PostAsJsonAsync(devices, new RegisterDeviceBody("fcm", "com.juice.app", token, null, "vi-VN"), Ct));
            var again = await OkAsync<DeviceResponse>(me.PostAsJsonAsync(devices, new RegisterDeviceBody("fcm", "com.juice.app", token, null, "en"), Ct));
            Assert.Equal((registered.Id, registered.CreatedAt, "en"), (again.Id, again.CreatedAt, again.Locale));
            // A refresh without a locale keeps the one it had.
            Assert.Equal("en", (await OkAsync<DeviceResponse>(me.PostAsJsonAsync(devices, new RegisterDeviceBody("fcm", "com.juice.app", token, null, null), Ct))).Locale);
            Assert.Contains(await OkAsync<List<DeviceResponse>>(me.GetAsync(devices, Ct)), d => d.Id == registered.Id);
            // Its token is kept encrypted.
            await using (var db = people.App.Database.Context())
            {
                Assert.DoesNotContain(token, (await db.Devices.AsNoTracking().SingleAsync(d => d.Id == registered.Id, Ct)).Token);
            }

            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),
                (await ProblemAsync(me.PostAsJsonAsync(devices, new RegisterDeviceBody("webpush", "web", token, null, null), Ct))));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest), (await ProblemAsync(me.PostAsJsonAsync(devices,
                new RegisterDeviceBody("webpush", "web", null, new WebPushSubscriptionBody("http://push.example.com/x", new string('A', 87), new string('B', 22)), null), Ct))));
            var web = await OkAsync<DeviceResponse>(me.PostAsJsonAsync(devices, new RegisterDeviceBody("webpush", "web", null,
                new WebPushSubscriptionBody($"https://push.example.com/{Guid.NewGuid():N}", new string('A', 87), new string('B', 22)), null), Ct));
            Assert.Equal("webpush", web.Platform);

            // Another person on the same device registers its token: it leaves the first.
            Guid other;
            await using (var db = people.App.Database.Context())
            {
                var person = new Subscriber { TenantId = TenantId, Source = "local", ExternalId = "dev-" + Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
                db.Subscribers.Add(person);
                await db.SaveChangesAsync(Ct);
                other = person.Id;
            }
            await using (var scope = people.App.Services.CreateAsyncScope())
            {
                var moved = await scope.ServiceProvider.GetRequiredService<DeviceRegistry>()
                    .RegisterAsync(TenantId, other, "fcm", "com.juice.app", token, null, null, Ct);
                Assert.Equal(registered.Id, moved.Value!.Id);
            }
            Assert.DoesNotContain(await OkAsync<List<DeviceResponse>>(me.GetAsync(devices, Ct)), d => d.Id == registered.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await me.DeleteAsync($"{devices}/{registered.Id}", Ct)).StatusCode);

            // Linked into the signed-in person, the other identity's registrations are theirs to see and remove.
            var linkedToken = "fcm:" + Guid.NewGuid().ToString("N");
            Guid linked;
            await using (var scope = people.App.Services.CreateAsyncScope())
            {
                linked = (await scope.ServiceProvider.GetRequiredService<DeviceRegistry>()
                    .RegisterAsync(TenantId, other, "fcm", "com.juice.app", linkedToken, null, null, Ct)).Value!.Id;
            }
            await using (var db = people.App.Database.Context())
            {
                var me_ = await db.Subscribers.AsNoTracking().Where(s => s.TenantId == TenantId && s.Source == "oidc").Select(s => s.Id).SingleAsync(Ct);
                await db.Subscribers.Where(s => s.Id == other).ExecuteUpdateAsync(s => s.SetProperty(x => x.MergedInto, me_), Ct);
            }
            Assert.Contains(await OkAsync<List<DeviceResponse>>(me.GetAsync(devices, Ct)), d => d.Id == linked);
            Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync($"{devices}/{linked}", Ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync($"{devices}/{web.Id}", Ct)).StatusCode);
            Assert.DoesNotContain(await OkAsync<List<DeviceResponse>>(me.GetAsync(devices, Ct)), d => d.Id == web.Id);

            // Unseen for 60 days: swept.
            await using (var db = people.App.Database.Context())
            {
                await db.Devices.Where(d => d.Id == registered.Id).ExecuteUpdateAsync(d => d.SetProperty(x => x.LastSeenAt, DateTimeOffset.UtcNow.AddDays(-61)), Ct);
            }
            var swept = await people.App.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<DeviceSweep>().Single().SweepAsync(Ct);
            Assert.True(swept.Devices >= 1);
            await using (var db = people.App.Database.Context())
            {
                Assert.False(await db.Devices.AnyAsync(d => d.Id == registered.Id, Ct));
            }
        }

        /// <summary>The service's own application is provisioned at start and can't be changed or used as a producer.</summary>
        [Fact]
        public async Task The_services_own_application_is_not_an_admins_to_change()
        {
            LocalStack.RequireAvailable();
            using var admin = people.App.Client(await LocalStack.AdminTokenAsync());
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ManagedByService), (await ProblemAsync(admin.PutAsJsonAsync(
                $"/api/v1/applications/{ServiceTypes.ApplicationKey}", new UpdateApplicationBody(null, null, false, null), Ct))));
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ManagedByService), (await ProblemAsync(admin.PostAsJsonAsync(
                "/api/v1/applications", new CreateApplicationBody(ServiceTypes.ApplicationKey, "Mine", "my-client"), Ct))));
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ManagedByService), (await ProblemAsync(admin.PutAsync(
                $"/api/v1/applications/{ServiceTypes.ApplicationKey}/tenants/{ProducerApp.OtherTenant}", null, Ct))));
            await using var db = people.App.Database.Context();
            var app = await db.Applications.AsNoTracking().SingleAsync(a => a.Key == ServiceTypes.ApplicationKey, Ct);
            Assert.Equal((ServiceTypes.ClientId, true), (app.ClientId, app.Enabled));
            var type = await db.Types.AsNoTracking().SingleAsync(t => t.ApplicationId == app.Id && t.Key == ServiceTypes.VerifyType, Ct);
            Assert.Equal((true, true), (type.Mandatory, type.ExternalRecipients));
            Assert.Equal(["data.code"], type.Redact);
        }
    }
}
