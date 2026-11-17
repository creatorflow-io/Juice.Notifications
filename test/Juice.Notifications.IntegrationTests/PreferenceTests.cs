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
    /// Preferences, quiet hours, mutes and the mandatory policy (phase-2-plan M14, design §6.4) in the real host: phase 2 proof point 1, then one
    /// test per row of the §6.4 table. The row "unverified contact point" is proof point 2, in <see cref="ContactPointTests"/>; "throttle and digest"
    /// waits for phase 3 (a <c>digest</c> preference acts as <c>on</c>, shown with the category opt-out). Each test declares its own types, so the
    /// tenant mutes it adds touch nothing else.
    /// </summary>
    public sealed class PreferenceTests(PeopleApp people) : IClassFixture<PeopleApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid TenantId = ProducerApp.Tenant;
        private static readonly string Tenant = $"/api/v1/tenants/{TenantId}";

        private static JsonElement Json(string json) => JsonElement.Parse(json);

        private static string Mailbox(string name) => $"{name}-{Guid.NewGuid():N}"[..(name.Length + 11)] + "@example.com";

        private static async Task<T> OkAsync<T>(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
        }

        private static async Task<(HttpStatusCode Status, string Code, string Body)> ProblemAsync(Task<HttpResponseMessage> call)
        {
            var response = await call;
            var body = await response.Content.ReadAsStringAsync(Ct);
            using var json = JsonDocument.Parse(body);
            return (response.StatusCode, json.RootElement.GetProperty("code").GetString()!, body);
        }

        /// <summary>A person reached through the fake resolver, with a verified email address, and their subscriber id.</summary>
        private sealed record Person(ResolvedPerson Resolved, Guid Id, string Address);

        /// <summary>Sets up the mail integration and the fake resolver, and declares a normal and a mandatory type of one new category.</summary>
        private async Task<(string Normal, string Mandatory, string Category)> TypesAsync(string templates = """{ "email": "Subject: Hello\n\nHello." }""")
        {
            using var admin = people.App.Client(await LocalStack.AdminTokenAsync());
            await OkAsync<JsonElement>(admin.PutAsJsonAsync($"{Tenant}/integrations/mail", new PutIntegrationBody("email", "smtp",
                Json("""{ "server": "local", "from": "notifications@juice.local" }"""), null, null, true), Ct));
            await OkAsync<ResolverResponse>(admin.PutAsJsonAsync($"{Tenant}/resolvers/crews", new PutResolverBody("fake", ["group", "user"], null, null, null), Ct));
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            var stem = $"{ProducerApp.Key}.p{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            var category = "c" + Guid.NewGuid().ToString("N")[..12];
            foreach (var (key, mandatory) in new[] { (stem + ".normal", false), (stem + ".mandatory", true) })
            {
                await OkAsync<JsonElement>(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types",
                    new DeclareTypeBody(key, "Hello", category, null, "normal", mandatory, null, Json("""{ "type": "object" }"""), null, null, null, Json(templates)), Ct));
            }
            return (stem + ".normal", stem + ".mandatory", category);
        }

        /// <summary>People in a new crew, each with a verified email address, as a plan maps them.</summary>
        private async Task<(string Crew, Person[] People)> CrewAsync(params string[] names)
        {
            var crew = $"crew-{Guid.NewGuid():N}";
            var resolved = names.Select(n => (Name: n, Address: Mailbox(n)))
                .Select(p => (p.Address, Person: new ResolvedPerson(FakePeopleResolver.Source, $"{p.Name}-{crew}", p.Name, Contacts: [new ResolvedContact("email", p.Address, true)])))
                .ToList();
            people.People.Groups[crew] = resolved.Select(p => p.Person).ToArray();
            var crypto = people.App.Services.GetRequiredService<ContactCrypto>();
            var channels = people.App.Services.GetRequiredService<IChannelRegistry>();
            await using var db = people.App.Database.Context();
            var map = await SubscriberDirectory.MapAsync(db, TenantId, resolved.Select(p => p.Person).ToList(), DateTimeOffset.UtcNow,
                new SourceContacts(crypto, channels, NullLogger.Instance), Ct);
            return (crew, resolved.Select(p => new Person(p.Person, map[(FakePeopleResolver.Source, p.Person.ExternalId)], p.Address)).ToArray());
        }

        private async Task PreferAsync(Person person, string category, string channel, string mode)
        {
            await using var db = people.App.Database.Context();
            db.Preferences.Add(new Preference { TenantId = TenantId, SubscriberId = person.Id, Category = category, Channel = channel, Mode = mode, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        private async Task MuteAsync(Person person, string match, DateTimeOffset? startsAt = null, DateTimeOffset? endsAt = null, bool cancelled = false)
        {
            await using var db = people.App.Database.Context();
            var now = DateTimeOffset.UtcNow;
            db.Mutes.Add(new Mute
            {
                TenantId = TenantId, SubscriberId = person.Id, Match = match, StartsAt = startsAt ?? now.AddMinutes(-1), EndsAt = endsAt, CreatedAt = now,
                CancelledAt = cancelled ? now : null,
            });
            await db.SaveChangesAsync(Ct);
        }

        /// <summary>Quiet hours in UTC from an hour ago until <paramref name="until"/> from now (to the minute), so now is quiet.</summary>
        private async Task<DateTimeOffset> QuietAsync(Person person, TimeSpan until)
        {
            var now = DateTimeOffset.UtcNow;
            var end = now + until;
            await using var db = people.App.Database.Context();
            Assert.Equal(1, await db.Subscribers.Where(s => s.TenantId == TenantId && s.Id == person.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.QuietHours,
                QuietHours.Store(new QuietHoursSpec(null, now.AddHours(-1).ToString("HH:mm"), end.ToString("HH:mm"), "UTC"))), Ct));
            return new DateTimeOffset(end.Year, end.Month, end.Day, end.Hour, end.Minute, 0, TimeSpan.Zero);
        }

        private async Task<Guid> SendAsync(string type, string crew, Dictionary<string, string>? labels = null)
        {
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            return (await OkAsync<AcceptedResponse>(producer.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null,
                Guid.NewGuid().ToString("N"), null, [new RecipientBody("group", Id: crew, Resolver: "crews")], null, null, labels, Json("{}"), null, null), Ct))).Id;
        }

        /// <summary>
        /// The request's recipients, by subscriber, once each is through with planning (done, or deferred) and its deliveries are sent, with the
        /// deliveries' statuses by subscriber.
        /// </summary>
        private async Task<Dictionary<Guid, (NotificationRecipient Recipient, List<(string Channel, DeliveryStatus Status)> Deliveries)>> PlannedAsync(Guid requestId, int count)
        {
            await using var db = people.App.Database.Context();
            for (var i = 0; i < 200; i++)
            {
                var recipients = await db.Recipients.AsNoTracking().Where(r => db.Notifications.Any(n => n.Id == r.NotificationId && n.RequestId == requestId)).ToListAsync(Ct);
                var deliveries = await (from item in db.DeliveryItems
                                        join d in db.Deliveries on new { item.TenantId, Id = item.DeliveryId } equals new { d.TenantId, d.Id }
                                        where item.RequestId == requestId
                                        select new { item.RecipientId, d.Channel, d.Status }).AsNoTracking().ToListAsync(Ct);
                if (recipients.Count == count && recipients.All(r => r.NextAt == null || r.Outcome == RecipientOutcome.Deferred)
                    && deliveries.All(d => d.Status is not (DeliveryStatus.Pending or DeliveryStatus.Sending or DeliveryStatus.Retrying)))
                {
                    return recipients.ToDictionary(r => r.SubscriberId!.Value,
                        r => (r, deliveries.Where(d => d.RecipientId == r.Id).Select(d => (d.Channel, d.Status)).OrderBy(d => d.Channel, StringComparer.Ordinal).ToList()));
                }
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException($"Request {requestId} wasn't planned.");
        }

        /// <summary>Reached in the inbox and by email (the item written, the email accepted), having gone past the rules named.</summary>
        private static void Reached((NotificationRecipient Recipient, List<(string Channel, DeliveryStatus Status)> Deliveries) planned, params string[] bypassed)
        {
            Assert.Equal(RecipientOutcome.Planned, planned.Recipient.Outcome);
            Assert.Equal(bypassed.Order(), planned.Recipient.Bypassed.Order());
            Assert.Equal([(Channels.Email, DeliveryStatus.Accepted), (Channels.Inbox, DeliveryStatus.Delivered)], planned.Deliveries);
        }

        /// <summary>Reached in the inbox only.</summary>
        private static void InboxOnly((NotificationRecipient Recipient, List<(string Channel, DeliveryStatus Status)> Deliveries) planned)
        {
            Assert.Equal(RecipientOutcome.Planned, planned.Recipient.Outcome);
            Assert.Equal([(Channels.Inbox, DeliveryStatus.Delivered)], planned.Deliveries);
        }

        private static void Held((NotificationRecipient Recipient, List<(string Channel, DeliveryStatus Status)> Deliveries) planned, RecipientOutcome outcome)
        {
            Assert.Equal((outcome, (DateTimeOffset?)null), (planned.Recipient.Outcome, planned.Recipient.NextAt));
            Assert.Empty(planned.Recipient.Bypassed);
            Assert.Empty(planned.Deliveries);
        }

        /// <summary>
        /// Phase 2 proof point 1: a person who opted out of a category, snoozed everything and is in their quiet hours still gets a mandatory type
        /// of it, in the inbox and by email, and the recipient records each rule it went past. A normal type of the category doesn't reach them.
        /// The item is in their inbox.
        /// </summary>
        [Fact]
        public async Task Mandatory_type_reaches_an_opted_out_person_in_the_inbox_and_records_the_bypass()
        {
            LocalStack.RequireAvailable();
            var (normal, mandatory, category) = await TypesAsync();
            var (crew, members) = await CrewAsync("pat");
            var pat = members[0];
            await PreferAsync(pat, category, "*", PreferenceModes.Off);
            await MuteAsync(pat, "{}", endsAt: DateTimeOffset.UtcNow.AddHours(1));
            await QuietAsync(pat, TimeSpan.FromHours(2));

            Reached((await PlannedAsync(await SendAsync(mandatory, crew), 1))[pat.Id], BypassedRules.OptOut, BypassedRules.PersonalMute, BypassedRules.QuietHours);
            Held((await PlannedAsync(await SendAsync(normal, crew), 1))[pat.Id], RecipientOutcome.OptedOut);
            await using var db = people.App.Database.Context();
            var item = await db.InboxItems.AsNoTracking().SingleAsync(i => i.TenantId == TenantId && i.SubscriberId == pat.Id, Ct);
            Assert.Equal((mandatory, "Hello", DisplayGroups.General), (item.TypeKey, item.Title, item.GroupKey));
        }

        /// <summary>
        /// §6.4 category opt-out: a category off on every channel holds a normal type; the category's own choice wins over one for every category
        /// (<c>*</c>); <c>digest</c> is on until phase 3. A mandatory type goes past it, recorded.
        /// </summary>
        [Fact]
        public async Task A_category_opt_out_holds_a_normal_type_and_a_mandatory_type_goes_past_it()
        {
            LocalStack.RequireAvailable();
            var (normal, mandatory, category) = await TypesAsync();
            var (crew, members) = await CrewAsync("ann", "ben", "cat", "dan", "eve", "fay");
            var ann = members[0];
            var ben = members[1];
            var cat = members[2];
            var dan = members[3];
            var eve = members[4];
            var fay = members[5];
            await PreferAsync(ann, category, "*", PreferenceModes.Off);
            await PreferAsync(ben, "*", "*", PreferenceModes.Off);
            await PreferAsync(cat, "*", "*", PreferenceModes.Off);
            await PreferAsync(cat, category, "*", PreferenceModes.On);
            await PreferAsync(dan, category, "*", PreferenceModes.Digest);
            await PreferAsync(eve, "other-category", "*", PreferenceModes.Off);
            // Everything off but this category by email: not opted out.
            await PreferAsync(fay, "*", "*", PreferenceModes.Off);
            await PreferAsync(fay, category, "email", PreferenceModes.On);

            var held = await PlannedAsync(await SendAsync(normal, crew), 6);
            Held(held[ann.Id], RecipientOutcome.OptedOut);
            Held(held[ben.Id], RecipientOutcome.OptedOut);
            Reached(held[cat.Id]);
            Reached(held[dan.Id]);
            Reached(held[eve.Id]);
            // Her inbox is off with everything else: the email alone.
            Assert.Equal(RecipientOutcome.Planned, held[fay.Id].Recipient.Outcome);
            Assert.Equal([(Channels.Email, DeliveryStatus.Accepted)], held[fay.Id].Deliveries);

            var bypassed = await PlannedAsync(await SendAsync(mandatory, crew), 6);
            Reached(bypassed[ann.Id], BypassedRules.OptOut);
            Reached(bypassed[ben.Id], BypassedRules.OptOut);
            Reached(bypassed[cat.Id]);
        }

        /// <summary>
        /// §6.4 quiet hours: a normal type's inbox item comes at once; its email waits, deferred until they end, and goes out then. A mandatory
        /// type doesn't wait (proof point 1 shows it recorded).
        /// </summary>
        [Fact]
        public async Task Quiet_hours_hold_personal_channels_until_they_end()
        {
            LocalStack.RequireAvailable();
            var (normal, _, _) = await TypesAsync();
            var (crew, members) = await CrewAsync("pat", "sam");
            var pat = members[0];
            var sam = members[1];
            var end = await QuietAsync(pat, TimeSpan.FromHours(2));

            var request = await SendAsync(normal, crew);
            var planned = await PlannedAsync(request, 2);
            Reached(planned[sam.Id]);
            var (deferred, deliveries) = planned[pat.Id];
            Assert.Equal(RecipientOutcome.Deferred, deferred.Outcome);
            Assert.InRange(deferred.NextAt!.Value, end.AddMinutes(-1), end.AddMinutes(1));
            Assert.Equal([(Channels.Inbox, DeliveryStatus.Delivered)], deliveries);

            // The hours end: the email goes out, and the recipient is planned.
            await using (var db = people.App.Database.Context())
            {
                await db.Subscribers.Where(s => s.Id == pat.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.QuietHours, (string?)null), Ct);
                await db.Recipients.Where(r => r.Id == deferred.Id).ExecuteUpdateAsync(r => r.SetProperty(x => x.NextAt, DateTimeOffset.UtcNow), Ct);
            }
            people.App.Services.GetRequiredService<Stages.StageNudges>().Wake(Planning.DeliveryStageNames.DeliveryPlanner);
            for (var i = 0; i < 200 && (await PlannedAsync(request, 2))[pat.Id].Recipient.Outcome == RecipientOutcome.Deferred; i++)
            {
                await Task.Delay(100, Ct);
            }
            Reached((await PlannedAsync(request, 2))[pat.Id]);
        }

        /// <summary>
        /// §6.4 personal mute or snooze: one in force that matches holds a normal type; one ended, cancelled, not started or not matching doesn't.
        /// A mandatory type goes past it, recorded.
        /// </summary>
        [Fact]
        public async Task A_personal_mute_holds_a_normal_type_and_a_mandatory_type_goes_past_it()
        {
            LocalStack.RequireAvailable();
            var (normal, mandatory, _) = await TypesAsync();
            var (crew, members) = await CrewAsync("ann", "ben", "cat", "dan", "eve", "fay");
            var ann = members[0];
            var ben = members[1];
            var cat = members[2];
            var dan = members[3];
            var eve = members[4];
            var fay = members[5];
            var now = DateTimeOffset.UtcNow;
            await MuteAsync(ann, $$"""{ "type": "{{normal}}" }""");
            await MuteAsync(ben, """{ "labels": { "env": "prod" } }""", endsAt: now.AddHours(1));
            await MuteAsync(cat, "{}", startsAt: now.AddHours(-2), endsAt: now.AddHours(-1));
            await MuteAsync(dan, "{}", cancelled: true);
            await MuteAsync(eve, "{}", startsAt: now.AddHours(1));
            await MuteAsync(fay, """{ "labels": { "env": "staging" } }""");

            var held = await PlannedAsync(await SendAsync(normal, crew, new() { ["env"] = "prod" }), 6);
            Held(held[ann.Id], RecipientOutcome.Muted);
            Held(held[ben.Id], RecipientOutcome.Muted);
            foreach (var reached in new[] { cat, dan, eve, fay })
            {
                Reached(held[reached.Id]);
            }

            var bypassed = await PlannedAsync(await SendAsync(mandatory, crew, new() { ["env"] = "prod" }), 6);
            Reached(bypassed[ann.Id]); // its mute names the normal type only
            Reached(bypassed[ben.Id], BypassedRules.PersonalMute);
            Reached(bypassed[cat.Id]);
        }

        /// <summary>
        /// §6.4 a channel turned off: respected for every type. Off for the category wins over on for every category, and the other way round. A
        /// person who turned off the inbox too is opted out of a normal type; a mandatory type keeps their inbox.
        /// </summary>
        [Fact]
        public async Task A_channel_turned_off_is_respected_even_for_a_mandatory_type()
        {
            LocalStack.RequireAvailable();
            var (normal, mandatory, category) = await TypesAsync();
            var (crew, members) = await CrewAsync("ann", "ben", "cat", "dan");
            var ann = members[0];
            var ben = members[1];
            var cat = members[2];
            var dan = members[3];
            await PreferAsync(ann, category, "email", PreferenceModes.Off);
            await PreferAsync(ben, "*", "email", PreferenceModes.Off);
            await PreferAsync(cat, "*", "email", PreferenceModes.Off);
            await PreferAsync(cat, category, "email", PreferenceModes.On);
            await PreferAsync(dan, category, "email", PreferenceModes.Off);
            await PreferAsync(dan, category, "inbox", PreferenceModes.Off);

            foreach (var type in new[] { normal, mandatory })
            {
                var planned = await PlannedAsync(await SendAsync(type, crew), 4);
                InboxOnly(planned[ann.Id]);
                InboxOnly(planned[ben.Id]);
                Reached(planned[cat.Id]);
                if (type == normal)
                {
                    Held(planned[dan.Id], RecipientOutcome.OptedOut);
                }
                else
                {
                    InboxOnly(planned[dan.Id]); // the inbox can't be turned off for a mandatory type
                }
            }
        }

        /// <summary>
        /// §6.4 a channel the type doesn't declare: a person's verified address on it is never used, mandatory or not. The inbox needs no template:
        /// a type without one shows its name (phase-2-plan I17).
        /// </summary>
        [Fact]
        public async Task A_channel_the_type_doesnt_declare_is_never_used()
        {
            LocalStack.RequireAvailable();
            var (normal, mandatory, _) = await TypesAsync("""{ "webhook": "{ \"hello\": true }" }""");
            var (crew, members) = await CrewAsync("pat");
            var pat = members[0];
            foreach (var type in new[] { normal, mandatory })
            {
                InboxOnly((await PlannedAsync(await SendAsync(type, crew), 1))[pat.Id]);
            }
        }

        /// <summary>
        /// §6.4 tenant mute: holds a normal type whatever it matches by; a mandatory type only when it names the type exactly (no wildcard), which
        /// its response flags. Cancelling it lets the type through again.
        /// </summary>
        [Fact]
        public async Task A_tenant_mute_holds_a_mandatory_type_only_when_it_names_it_exactly()
        {
            LocalStack.RequireAvailable();
            var (normal, mandatory, _) = await TypesAsync();
            var (crew, members) = await CrewAsync("pat");
            var pat = members[0];
            using var admin = people.App.Client(await LocalStack.AdminTokenAsync());
            var stem = normal[..normal.LastIndexOf('.')];

            var wildcard = await OkAsync<MuteResponse>(admin.PostAsJsonAsync($"{Tenant}/mutes",
                new AddMuteBody(Json($$"""{ "type": "{{stem}}.*" }"""), null, DateTimeOffset.UtcNow.AddHours(1), "Maintenance"), Ct));
            Assert.False(wildcard.AppliesToMandatory);
            Assert.Equal("Maintenance", wildcard.Reason);
            Held((await PlannedAsync(await SendAsync(normal, crew), 1))[pat.Id], RecipientOutcome.Muted);
            Reached((await PlannedAsync(await SendAsync(mandatory, crew), 1))[pat.Id], BypassedRules.TenantMute);

            var exact = await OkAsync<MuteResponse>(admin.PostAsJsonAsync($"{Tenant}/mutes", new AddMuteBody(Json($$"""{ "type": "{{mandatory}}" }"""), null, null, null), Ct));
            Assert.True(exact.AppliesToMandatory);
            Assert.Contains(await OkAsync<List<MuteResponse>>(admin.GetAsync($"{Tenant}/mutes", Ct)), m => m.Id == exact.Id && m.AppliesToMandatory);
            Held((await PlannedAsync(await SendAsync(mandatory, crew), 1))[pat.Id], RecipientOutcome.Muted);

            foreach (var mute in new[] { exact, wildcard })
            {
                Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Tenant}/mutes/{mute.Id}", Ct)).StatusCode);
            }
            Assert.DoesNotContain(await OkAsync<List<MuteResponse>>(admin.GetAsync($"{Tenant}/mutes", Ct)), m => m.Id == exact.Id);
            Assert.Contains(await OkAsync<List<MuteResponse>>(admin.GetAsync($"{Tenant}/mutes?all=true", Ct)), m => m.Id == exact.Id && m.CancelledAt != null);
            Reached((await PlannedAsync(await SendAsync(mandatory, crew), 1))[pat.Id]);
            Reached((await PlannedAsync(await SendAsync(normal, crew), 1))[pat.Id]);
        }

        /// <summary>The person's preferences, quiet hours and mutes through <c>me</c>, checked as they're saved.</summary>
        [Fact]
        public async Task A_person_sets_preferences_quiet_hours_and_mutes()
        {
            LocalStack.RequireAvailable();
            using var me = people.App.Client(await LocalStack.PersonTokenAsync());
            using var admin = people.App.Client(await LocalStack.AdminTokenAsync());
            var self = await OkAsync<MeResponse>(me.GetAsync($"{Tenant}/me", Ct));
            Assert.Equal("oidc", self.Source);

            var saved = await OkAsync<List<PreferenceResponse>>(me.PutAsJsonAsync($"{Tenant}/me/preferences", new PutPreferencesBody(
                [new("security", "*", "on"), new("*", "email", "off"), new("billing", "inbox", "digest")]), Ct));
            Assert.Equal([("*", "email", "off"), ("billing", "inbox", "digest"), ("security", "*", "on")],
                saved.Select(p => (p.Category, p.Channel, p.Mode)).ToList());
            foreach (var bad in new PreferenceBody[] { new("security", "webhook", "on"), new("Security", "*", "on"), new("security", "*", "sometimes") })
            {
                Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),
                    ((await ProblemAsync(me.PutAsJsonAsync($"{Tenant}/me/preferences", new PutPreferencesBody([bad]), Ct))) is var p ? (p.Status, p.Code) : default));
            }
            Assert.Equal(HttpStatusCode.BadRequest, (await ProblemAsync(me.PutAsJsonAsync($"{Tenant}/me/preferences",
                new PutPreferencesBody([new("security", "*", "on"), new("security", "*", "off")]), Ct))).Status);
            // A body without the list is refused, not taken as "everything on".
            Assert.Equal(HttpStatusCode.BadRequest, (await ProblemAsync(me.PutAsJsonAsync($"{Tenant}/me/preferences", new PutPreferencesBody(null), Ct))).Status);
            Assert.Equal(3, (await OkAsync<List<PreferenceResponse>>(me.GetAsync($"{Tenant}/me/preferences", Ct))).Count);
            var replaced = await OkAsync<List<PreferenceResponse>>(me.PutAsJsonAsync($"{Tenant}/me/preferences", new PutPreferencesBody([new("security", "*", "off")]), Ct));
            Assert.Equal(("security", "*", "off"), Assert.Single(replaced) is var only ? (only.Category, only.Channel, only.Mode) : default);
            Assert.Single(await OkAsync<List<PreferenceResponse>>(me.GetAsync($"{Tenant}/me/preferences", Ct)));

            var quiet = await OkAsync<QuietHoursResponse>(me.PutAsJsonAsync($"{Tenant}/me/quiet-hours",
                new QuietHoursBody(["fri", "mon"], "22:00", "07:00", "Europe/Berlin"), Ct));
            Assert.Equal(["mon", "fri"], quiet.Days!);
            Assert.Equal(("22:00", "07:00", "Europe/Berlin"), (quiet.Start, quiet.End, quiet.TimeZone));
            Assert.Equal(quiet.Start, (await OkAsync<MeResponse>(me.GetAsync($"{Tenant}/me", Ct))).QuietHours?.Start);
            var refused = await ProblemAsync(me.PutAsJsonAsync($"{Tenant}/me/quiet-hours", new QuietHoursBody(["someday"], "22:00", "22:00", "Not/AZone"), Ct));
            Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
            foreach (var pointer in new[] { "/quietHours/days", "/quietHours/end", "/quietHours/timeZone" })
            {
                Assert.Contains(pointer, refused.Body);
            }
            Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync($"{Tenant}/me/quiet-hours", Ct)).StatusCode);
            Assert.Null((await OkAsync<MeResponse>(me.GetAsync($"{Tenant}/me", Ct))).QuietHours);

            var snooze = await OkAsync<MuteResponse>(me.PostAsJsonAsync($"{Tenant}/me/mutes", new AddMuteBody(null, null, DateTimeOffset.UtcNow.AddHours(1), null), Ct));
            Assert.Equal(("self", false, JsonValueKind.Object), (snooze.CreatedBy, snooze.AppliesToMandatory, snooze.Match.ValueKind));
            Assert.Contains(await OkAsync<List<MuteResponse>>(me.GetAsync($"{Tenant}/me/mutes", Ct)), m => m.Id == snooze.Id);
            Assert.Equal(HttpStatusCode.BadRequest, (await ProblemAsync(me.PostAsJsonAsync($"{Tenant}/me/mutes",
                new AddMuteBody(Json("""{ "type": "Not A Pattern" }"""), null, null, null), Ct))).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await ProblemAsync(me.PostAsJsonAsync($"{Tenant}/me/mutes",
                new AddMuteBody(null, null, DateTimeOffset.UtcNow.AddHours(-1), null), Ct))).Status);
            // A NUL can't be stored (jsonb): refused with a pointer, not a failed save.
            Assert.Equal(HttpStatusCode.BadRequest, (await ProblemAsync(me.PostAsJsonAsync($"{Tenant}/me/mutes",
                new AddMuteBody(Json("""{ "labels": { "env": "a\u0000" } }"""), null, null, null), Ct))).Status);
            // Nor half a surrogate pair (sent as written: a serializer may refuse to write it).
            Assert.Equal(HttpStatusCode.BadRequest, (await ProblemAsync(me.PostAsync($"{Tenant}/me/mutes",
                new StringContent("""{ "match": { "type": "\ud800" } }""", System.Text.Encoding.UTF8, "application/json"), Ct))).Status);
            // The service's own types aren't muted, so a mute naming one is refused.
            Assert.Equal(HttpStatusCode.BadRequest, (await ProblemAsync(admin.PostAsJsonAsync($"{Tenant}/mutes",
                new AddMuteBody(Json($$"""{ "type": "{{ServiceTypes.VerifyType}}" }"""), null, null, null), Ct))).Status);
            // A person's mute is theirs: the tenant's mutes don't list or cancel it.
            Assert.DoesNotContain(await OkAsync<List<MuteResponse>>(admin.GetAsync($"{Tenant}/mutes?all=true", Ct)), m => m.Id == snooze.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{Tenant}/mutes/{snooze.Id}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync($"{Tenant}/me/mutes/{snooze.Id}", Ct)).StatusCode);
            Assert.DoesNotContain(await OkAsync<List<MuteResponse>>(me.GetAsync($"{Tenant}/me/mutes", Ct)), m => m.Id == snooze.Id);

            await OkAsync<List<PreferenceResponse>>(me.PutAsJsonAsync($"{Tenant}/me/preferences", new PutPreferencesBody([]), Ct));
        }
    }
}
