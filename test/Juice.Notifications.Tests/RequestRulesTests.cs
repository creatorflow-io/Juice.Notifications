using Juice.Notifications.Data;
using Juice.Notifications.Intake;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// A request's recipients, labels, links and actor (phase-1-plan M2), checked directly at their limits: each limit is allowed at its value and
    /// refused one past it, and each refusal names its field. Added after mutation testing showed the HTTP tests left these edges unchecked.
    /// </summary>
    public sealed class RequestRulesTests
    {
        private static readonly IChannelRegistry Channels = new ChannelRegistry([BuiltInChannels.Webhook, BuiltInChannels.Email]);

        private static NotificationType Type(bool external = true) => new()
        {
            Key = "app.thing", Version = 3, Name = "Thing", Category = "test", DisplayGroup = "general", Schema = "{}", SchemaHash = "h",
            Workflow = "[]", DeclarationHash = "d", ExternalRecipients = external,
        };

        private static (IReadOnlyList<RecipientSpec>? Result, List<FieldError> Invalid, List<FieldError> Refused) Recipients(
            IReadOnlyList<RecipientSpec>? sent, bool external = true, IReadOnlySet<string>? templated = null)
        {
            List<FieldError> invalid = [], refused = [];
            var result = RequestRules.Recipients(sent, Type(external), Channels, templated, invalid, refused);
            return (result, invalid, refused);
        }

        private static RecipientSpec Address(string address, string channel = "email") => new(RecipientKinds.Address, channel, address);

        [Fact]
        public void No_recipients_means_the_routes()
        {
            Assert.Equal([new RecipientSpec(RecipientKinds.Route)], Recipients(null).Result!);
            Assert.Equal([new RecipientSpec(RecipientKinds.Route)], Recipients([]).Result!);
        }

        [Fact]
        public void Up_to_a_hundred_recipients()
        {
            var hundred = Enumerable.Range(0, RequestRules.MaxRecipients).Select(i => Address($"u{i}@example.com")).ToList();
            var (result, invalid, _) = Recipients(hundred);
            Assert.Equal((RequestRules.MaxRecipients, 0), (result!.Count, invalid.Count));

            (result, invalid, _) = Recipients([.. hundred, Address("one-more@example.com")]);
            Assert.Null(result);
            Assert.Equal("/recipients", Assert.Single(invalid).Pointer);
        }

        [Fact]
        public void The_same_recipient_twice_is_one()
        {
            var (result, _, _) = Recipients([new(RecipientKinds.Route), Address("A@Example.com"), new(RecipientKinds.Route), Address(" a@example.com ")]);
            Assert.Equal([new RecipientSpec(RecipientKinds.Route), Address("a@example.com")], result!);
            // Different addresses, or the same one on another channel, are each kept.
            (result, _, _) = Recipients([Address("a@example.com"), Address("b@example.com")]);
            Assert.Equal(2, result!.Count);
        }

        [Fact]
        public void Each_refusal_names_its_recipient()
        {
            var (_, invalid, refused) = Recipients([
                Address("a@example.com", channel: "sms"),          // 0: no such channel
                new(RecipientKinds.Address, null, "a@example.com"),  // 1: no channel
                Address("not an address"),                           // 2
                Address("https://x.example.com", channel: "webhook"),// 3: webhook takes no addresses in a request
                new("group"),                                        // 4: a later kind
                new("nobody"),                                       // 5: no such kind
                new(null),                                           // 6
            ]);
            Assert.Equal(["/recipients/0/channel", "/recipients/1/channel", "/recipients/2/address", "/recipients/3/address", "/recipients/5/kind",
                "/recipients/6/kind"], invalid.Select(e => e.Pointer));
            Assert.Contains("use a route", invalid[3].Message);
            Assert.Contains("email address", invalid[2].Message);
            Assert.Equal("/recipients/4/kind", Assert.Single(refused).Pointer);
        }

        [Fact]
        public void External_recipients_need_the_type_to_allow_them_and_a_template()
        {
            var (_, invalid, refused) = Recipients([Address("a@example.com")], external: false);
            Assert.Empty(invalid);
            Assert.Equal("/recipients/0", Assert.Single(refused).Pointer);

            (_, _, refused) = Recipients([Address("a@example.com")], templated: new HashSet<string> { "webhook" });
            Assert.Equal("/recipients/0/channel", Assert.Single(refused).Pointer);
            (_, _, refused) = Recipients([Address("a@example.com")], templated: new HashSet<string> { "email" });
            Assert.Empty(refused);
        }

        [Theory]
        [InlineData("a@example.com", "a@example.com")]
        [InlineData("  A@Example.COM ", "a@example.com")]
        [InlineData("Ana <a@example.com>", null)]
        [InlineData("a@example.com, b@example.com", null)]
        [InlineData("a@example.com\n", null)]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData(null, null)]
        public void An_email_address_is_bare_and_lowercased(string? sent, string? expected)
            => Assert.Equal(expected, RequestRules.NormalizeAddress(BuiltInChannels.Email, sent));

        [Fact]
        public void An_address_is_at_most_320_characters()
        {
            var local = new string('a', 64);
            var at320 = local + "@" + string.Join(".", Enumerable.Repeat(new string('d', 60), 4)) + "." + new string('e', 11);
            Assert.Equal(320, at320.Length);
            Assert.NotNull(RequestRules.NormalizeAddress(BuiltInChannels.Email, at320));
            Assert.Null(RequestRules.NormalizeAddress(BuiltInChannels.Email, at320 + "e"));
            Assert.Null(RequestRules.NormalizeAddress(BuiltInChannels.Webhook, "a@example.com"));
        }

        private static List<FieldError> Labels(Dictionary<string, string> labels)
        {
            List<FieldError> invalid = [];
            RequestRules.Labels(labels, invalid);
            return invalid;
        }

        [Fact]
        public void Labels_up_to_fifty_with_values_up_to_500_characters()
        {
            var fifty = Enumerable.Range(0, RequestRules.MaxLabels).ToDictionary(i => $"l{i}", _ => new string('v', 500));
            Assert.Empty(Labels(fifty));
            Assert.Equal(["/labels"], Labels(new(fifty) { ["l50"] = "v" }).Select(e => e.Pointer));
            Assert.Equal(["/labels/a"], Labels(new() { ["a"] = new string('v', 501) }).Select(e => e.Pointer));
            Assert.Equal(["/labels/1a", "/labels/a~1b"], Labels(new() { ["1a"] = "v", ["a/b"] = "v", ["a.b:c-d_e"] = "v" }).Select(e => e.Pointer));
            Assert.Equal(RequestRules.NulMessage, Assert.Single(Labels(new() { ["a"] = "x\0" })).Message);
            Assert.Empty(Labels(new() { ["a"] = "" }));
        }

        private static List<FieldError> Links(Dictionary<string, string> links)
        {
            List<FieldError> invalid = [];
            RequestRules.Links(links, invalid);
            return invalid;
        }

        [Fact]
        public void Links_up_to_twenty_http_urls_of_up_to_2000_characters()
        {
            var twenty = Enumerable.Range(0, RequestRules.MaxLinks).ToDictionary(i => $"l{i}", i => $"https://example.com/{i}");
            Assert.Empty(Links(twenty));
            Assert.Equal(["/links"], Links(new(twenty) { ["l20"] = "https://example.com" }).Select(e => e.Pointer));
            var at2000 = "https://example.com/" + new string('p', 2000 - 20);
            Assert.Empty(Links(new() { ["a"] = at2000 }));
            Assert.Equal(["/links/a"], Links(new() { ["a"] = at2000 + "p" }).Select(e => e.Pointer));
            Assert.Equal(["/links/a", "/links/b", "/links/c", "/links/1d", "/links/e~1f"], Links(new()
            {
                ["a"] = "ftp://example.com", ["b"] = "/relative", ["c"] = "https://example.com/\nx", ["1d"] = "https://example.com", ["e/f"] = "https://example.com",
                ["ok.name-1_x"] = "http://example.com",
            }).Select(e => e.Pointer));
        }

        private static (string? Actor, List<FieldError> Invalid) Actor(ActorSpec? actor)
        {
            List<FieldError> invalid = [];
            return (RequestRules.Actor(actor, invalid), invalid);
        }

        [Theory]
        [InlineData("user", "u1", "user:u1")]
        [InlineData("system", "s", "system:s")]
        [InlineData("service", "svc", "service:svc")]
        public void An_actor_is_its_kind_and_id(string kind, string id, string expected)
            => Assert.Equal((expected, 0), (Actor(new ActorSpec(kind, id)).Actor, Actor(new ActorSpec(kind, id)).Invalid.Count));

        [Fact]
        public void An_actors_kind_and_id_are_checked()
        {
            Assert.Equal((null, 0), (Actor(null).Actor, Actor(null).Invalid.Count));
            Assert.Equal("/actor/kind", Assert.Single(Actor(new ActorSpec("robot", "x")).Invalid).Pointer);
            Assert.Equal("/actor/kind", Assert.Single(Actor(new ActorSpec(null, "x")).Invalid).Pointer);
            Assert.Equal("user:" + new string('i', 200), Actor(new ActorSpec("user", new string('i', 200))).Actor);
            foreach (var id in new[] { "", null, new string('i', 201), "a\tb" })
            {
                var (actor, invalid) = Actor(new ActorSpec("user", id));
                Assert.Null(actor);
                Assert.Equal("/actor/id", Assert.Single(invalid).Pointer);
            }
        }
    }
}
