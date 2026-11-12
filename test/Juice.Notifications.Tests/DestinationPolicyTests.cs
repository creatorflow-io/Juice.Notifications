using System.Net;
using Juice.Notifications.Providers;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// The SSRF rules every webhook connection passes (design §13.1, D16): each address class, the IPv6 forms that carry an IPv4 address, mixed
    /// DNS answers, allowlist host and port matching, the plain-HTTP rule and the allowlist entry check. Also the reference signature verifier
    /// receivers port (§10.4).
    /// </summary>
    public sealed class DestinationPolicyTests
    {
        private static readonly AllowedDestination[] None = [];

        private static IPAddress[] Ips(params string[] addresses) => addresses.Select(IPAddress.Parse).ToArray();

        private static AllowedDestination Entry(string destination, params int[] ports) => new(destination, ports);

        private static string? Check(string scheme, string host, int port, string[] addresses, AllowedDestination[]? allowlist = null, bool allowHttp = false)
            => DestinationPolicy.Check(scheme, host, port, Ips(addresses), allowlist ?? None, allowHttp);

        /// <summary>Loopback, link-local (cloud metadata), unspecified, documentation, multicast, reserved and broadcast are never reachable.</summary>
        [Theory]
        [InlineData("0.0.0.0")]
        [InlineData("0.1.2.3")]
        [InlineData("127.0.0.1")]
        [InlineData("127.255.255.254")]
        [InlineData("169.254.0.1")]
        [InlineData("169.254.169.254")]
        [InlineData("192.0.2.10")]
        [InlineData("198.51.100.7")]
        [InlineData("203.0.113.9")]
        [InlineData("224.0.0.1")]
        [InlineData("239.255.255.250")]
        [InlineData("240.0.0.1")]
        [InlineData("255.255.255.255")]
        [InlineData("::")]
        [InlineData("::1")]
        [InlineData("fe80::1")]
        [InlineData("febf::1")]
        [InlineData("ff02::1")]
        [InlineData("fd00:ec2::254")]
        [InlineData("2001:db8::1")]
        public void Blocked_ranges_are_blocked(string address) => Assert.Equal(DestinationClass.Blocked, DestinationPolicy.Classify(IPAddress.Parse(address)));

        /// <summary>RFC 1918, carrier-grade NAT, benchmarking, IETF protocol assignments and IPv6 unique-local are private: allowlist only.</summary>
        [Theory]
        [InlineData("10.0.0.1")]
        [InlineData("10.255.255.255")]
        [InlineData("172.16.0.1")]
        [InlineData("172.31.255.255")]
        [InlineData("192.168.1.1")]
        [InlineData("100.64.0.1")]
        [InlineData("100.127.255.255")]
        [InlineData("198.18.0.1")]
        [InlineData("198.19.255.255")]
        [InlineData("192.0.0.8")]
        [InlineData("fc00::1")]
        [InlineData("fd12:3456::1")]
        public void Private_ranges_are_private(string address) => Assert.Equal(DestinationClass.Private, DestinationPolicy.Classify(IPAddress.Parse(address)));

        /// <summary>Addresses just outside each private or blocked range, and ordinary global unicast, are public.</summary>
        [Theory]
        [InlineData("8.8.8.8")]
        [InlineData("1.1.1.1")]
        [InlineData("1.0.0.0")]
        [InlineData("9.255.255.255")]
        [InlineData("11.0.0.0")]
        [InlineData("126.255.255.255")]
        [InlineData("128.0.0.0")]
        [InlineData("169.253.255.255")]
        [InlineData("169.255.0.0")]
        [InlineData("172.15.255.255")]
        [InlineData("172.32.0.0")]
        [InlineData("192.167.255.255")]
        [InlineData("192.169.0.0")]
        [InlineData("100.63.255.255")]
        [InlineData("100.128.0.0")]
        [InlineData("198.17.255.255")]
        [InlineData("198.20.0.0")]
        [InlineData("192.0.1.0")]
        [InlineData("223.255.255.255")]
        [InlineData("2606:4700:4700::1111")]
        [InlineData("2a00:1450:4001::200e")]
        public void Other_addresses_are_public(string address) => Assert.Equal(DestinationClass.Public, DestinationPolicy.Classify(IPAddress.Parse(address)));

        /// <summary>
        /// IPv6 forms that carry an IPv4 address: IPv4-mapped is the IPv4 address; NAT64 (well-known and local-use) and 6to4 are the stricter of
        /// their own class and the embedded address's; IPv4-compatible is never reachable.
        /// </summary>
        [Theory]
        // IPv4-mapped: judged as the IPv4 address.
        [InlineData("::ffff:127.0.0.1", DestinationClass.Blocked)]
        [InlineData("::ffff:169.254.169.254", DestinationClass.Blocked)]
        [InlineData("::ffff:10.0.0.1", DestinationClass.Private)]
        [InlineData("::ffff:8.8.8.8", DestinationClass.Public)]
        // NAT64 well-known prefix 64:ff9b::/96: public itself, so the embedded class decides.
        [InlineData("64:ff9b::a9fe:a9fe", DestinationClass.Blocked)]
        [InlineData("64:ff9b::169.254.169.254", DestinationClass.Blocked)]
        [InlineData("64:ff9b::7f00:1", DestinationClass.Blocked)]
        [InlineData("64:ff9b::a00:1", DestinationClass.Private)]
        [InlineData("64:ff9b::c0a8:101", DestinationClass.Private)]
        [InlineData("64:ff9b::808:808", DestinationClass.Public)]
        // NAT64 local-use 64:ff9b:1::/48: private itself, blocked when it embeds a blocked address.
        [InlineData("64:ff9b:1::808:808", DestinationClass.Private)]
        [InlineData("64:ff9b:1::a00:1", DestinationClass.Private)]
        [InlineData("64:ff9b:1::a9fe:a9fe", DestinationClass.Blocked)]
        [InlineData("64:ff9b:1:ffff::7f00:1", DestinationClass.Blocked)]
        // 6to4 2002::/16 embeds the IPv4 address in bits 16–47.
        [InlineData("2002:7f00:1::1", DestinationClass.Blocked)]
        [InlineData("2002:a9fe:a9fe::1", DestinationClass.Blocked)]
        [InlineData("2002:a00:1::1", DestinationClass.Private)]
        [InlineData("2002:c0a8:101::1", DestinationClass.Private)]
        [InlineData("2002:808:808::1", DestinationClass.Public)]
        // IPv4-compatible ::/96 (deprecated): never reachable, whatever it embeds.
        [InlineData("::127.0.0.1", DestinationClass.Blocked)]
        [InlineData("::8.8.8.8", DestinationClass.Blocked)]
        [InlineData("::a00:1", DestinationClass.Blocked)]
        public void IPv6_forms_that_carry_IPv4_are_judged_by_the_stricter_class(string address, DestinationClass expected)
            => Assert.Equal(expected, DestinationPolicy.Classify(IPAddress.Parse(address)));

        /// <summary>A public address on 443 over https needs nothing; a host with no addresses is refused.</summary>
        [Fact]
        public void A_public_address_on_443_passes_and_no_address_is_refused()
        {
            Assert.Null(Check("https", "hooks.example.com", 443, ["8.8.8.8"]));
            Assert.Null(Check("https", "hooks.example.com", 443, ["2606:4700:4700::1111"]));
            Assert.Equal("no_address", Check("https", "hooks.example.com", 443, []));
        }

        /// <summary>Blocked addresses are refused whatever the allowlist says, and an IPv4-mapped one is reported as its IPv4 address.</summary>
        [Theory]
        [InlineData("127.0.0.1", "127.0.0.0/8", "blocked_address:127.0.0.1")]
        [InlineData("169.254.169.254", "169.254.0.0/16", "blocked_address:169.254.169.254")]
        [InlineData("::ffff:127.0.0.1", "127.0.0.0/8", "blocked_address:127.0.0.1")]
        [InlineData("::1", "::1/128", "blocked_address:::1")]
        [InlineData("64:ff9b::a9fe:a9fe", "64:ff9b::/96", "blocked_address:64:ff9b::a9fe:a9fe")]
        [InlineData("127.0.0.1", "localhost", "blocked_address:127.0.0.1")]
        public void Blocked_addresses_are_refused_even_when_allowlisted(string address, string entry, string expected)
        {
            Assert.Equal(expected, Check("https", "localhost", 443, [address], [Entry(entry, 443)]));
            Assert.Equal(expected, Check("http", "localhost", 8080, [address], [Entry(entry, 8080)], allowHttp: true));
        }

        /// <summary>A private address needs an allowlist entry covering both the address (or host) and the port.</summary>
        [Fact]
        public void A_private_address_needs_an_entry_for_its_address_and_port()
        {
            Assert.Equal("private_address_not_allowlisted:10.1.2.3", Check("https", "hooks.internal", 443, ["10.1.2.3"]));
            Assert.Equal("private_address_not_allowlisted:10.1.2.3", Check("https", "hooks.internal", 443, ["::ffff:10.1.2.3"]));
            Assert.Null(Check("https", "hooks.internal", 443, ["10.1.2.3"], [Entry("10.0.0.0/8")]));
            Assert.Null(Check("https", "hooks.internal", 8443, ["10.1.2.3"], [Entry("10.0.0.0/8", 8443)]));
            Assert.Null(Check("https", "hooks.internal", 8443, ["::ffff:10.1.2.3"], [Entry("10.0.0.0/8", 8443)]));
            Assert.Null(Check("https", "hooks.internal", 8443, ["10.1.2.3"], [Entry("hooks.internal", 8443)]));
            // Wrong port: an entry with no ports means 443 only.
            Assert.Equal("private_address_not_allowlisted:10.1.2.3", Check("https", "hooks.internal", 8443, ["10.1.2.3"], [Entry("10.0.0.0/8")]));
            Assert.Equal("private_address_not_allowlisted:10.1.2.3", Check("https", "hooks.internal", 8444, ["10.1.2.3"], [Entry("10.0.0.0/8", 8443)]));
            // Wrong network or wrong host.
            Assert.Equal("private_address_not_allowlisted:10.1.2.3", Check("https", "hooks.internal", 443, ["10.1.2.3"], [Entry("10.2.0.0/16")]));
            Assert.Equal("private_address_not_allowlisted:10.1.2.3", Check("https", "hooks.internal", 443, ["10.1.2.3"], [Entry("other.internal")]));
            Assert.Equal("private_address_not_allowlisted:10.1.2.3", Check("https", "hooks.internal", 443, ["10.1.2.3"], [Entry("internal")]));
            Assert.Equal("private_address_not_allowlisted:10.1.2.3", Check("https", "a.hooks.internal", 443, ["10.1.2.3"], [Entry("hooks.internal")]));
        }

        /// <summary>A public address off 443 needs an entry naming its host or address with that port: <c>port_not_allowed</c> otherwise.</summary>
        [Fact]
        public void A_public_address_off_443_needs_an_entry_with_the_port()
        {
            Assert.Equal("port_not_allowed:22", Check("https", "8.8.8.8", 22, ["8.8.8.8"]));
            Assert.Equal("port_not_allowed:8443", Check("https", "hooks.example.com", 8443, ["8.8.8.8"]));
            Assert.Equal("port_not_allowed:8443", Check("https", "hooks.example.com", 8443, ["8.8.8.8"], [Entry("8.8.8.0/24")]));
            Assert.Equal("port_not_allowed:8443", Check("https", "hooks.example.com", 8443, ["8.8.8.8"], [Entry("8.8.8.0/24", 9443)]));
            Assert.Equal("port_not_allowed:8443", Check("https", "hooks.example.com", 8443, ["8.8.8.8"], [Entry("other.example.com", 8443)]));
            Assert.Equal("port_not_allowed:8443", Check("https", "hooks.example.com", 8443, ["8.8.8.8"], [Entry("8.8.4.0/24", 8443)]));
            Assert.Null(Check("https", "hooks.example.com", 8443, ["8.8.8.8"], [Entry("8.8.8.0/24", 8443)]));
            Assert.Null(Check("https", "hooks.example.com", 8443, ["8.8.8.8"], [Entry("8.8.8.8/32", 443, 8443)]));
            Assert.Null(Check("https", "hooks.example.com", 8443, ["8.8.8.8"], [Entry("HOOKS.Example.com", 8443)]));
            // An entry for another port doesn't take 443 away.
            Assert.Null(Check("https", "hooks.example.com", 443, ["8.8.8.8"], [Entry("8.8.8.0/24", 8443)]));
        }

        /// <summary>A trailing dot names the same host, on the entry's side or the request's.</summary>
        [Theory]
        [InlineData("hooks.internal.", "hooks.internal")]
        [InlineData("hooks.internal", "hooks.internal.")]
        [InlineData("hooks.internal.", "HOOKS.internal.")]
        public void A_trailing_dot_names_the_same_host(string entry, string host)
            => Assert.Null(Check("https", host, 8443, ["10.1.2.3"], [Entry(entry, 8443)]));

        /// <summary>
        /// Every address a host resolves to is checked: one blocked or unallowlisted address rejects the destination, wherever it comes in the
        /// answer, so DNS mixing public and internal answers can't reach inside.
        /// </summary>
        [Theory]
        [InlineData(new[] { "8.8.8.8", "127.0.0.1" }, "blocked_address:127.0.0.1")]
        [InlineData(new[] { "127.0.0.1", "8.8.8.8" }, "blocked_address:127.0.0.1")]
        [InlineData(new[] { "8.8.8.8", "169.254.169.254" }, "blocked_address:169.254.169.254")]
        [InlineData(new[] { "2606:4700:4700::1111", "::ffff:127.0.0.1" }, "blocked_address:127.0.0.1")]
        [InlineData(new[] { "8.8.8.8", "64:ff9b::a9fe:a9fe" }, "blocked_address:64:ff9b::a9fe:a9fe")]
        [InlineData(new[] { "8.8.8.8", "10.0.0.1" }, "private_address_not_allowlisted:10.0.0.1")]
        [InlineData(new[] { "8.8.8.8", "fd00::1" }, "private_address_not_allowlisted:fd00::1")]
        public void One_bad_address_in_a_DNS_answer_rejects_the_destination(string[] addresses, string expected)
            => Assert.Equal(expected, Check("https", "mixed.example.com", 443, addresses));

        /// <summary>An allowlist entry for the private address doesn't let a blocked address in the same answer through.</summary>
        [Fact]
        public void An_allowlisted_private_address_does_not_excuse_a_blocked_one_beside_it()
        {
            Assert.Equal("blocked_address:169.254.169.254", Check("https", "mixed.internal", 443, ["10.0.0.1", "169.254.169.254"], [Entry("10.0.0.0/8")]));
            Assert.Equal("blocked_address:127.0.0.1", Check("https", "mixed.internal", 443, ["10.0.0.1", "127.0.0.1"], [Entry("mixed.internal")]));
            Assert.Equal("private_address_not_allowlisted:192.168.0.1", Check("https", "mixed.internal", 443, ["10.0.0.1", "192.168.0.1"], [Entry("10.0.0.0/8")]));
            Assert.Null(Check("https", "mixed.internal", 443, ["10.0.0.1", "192.168.0.1"], [Entry("mixed.internal")]));
            Assert.Null(Check("https", "mixed.internal", 443, ["10.0.0.1", "192.168.0.1"], [Entry("10.0.0.0/8"), Entry("192.168.0.0/16")]));
        }

        /// <summary>Plain http only to an allowlisted private address, and only when the deployment allows it.</summary>
        [Fact]
        public void Plain_http_only_to_an_allowlisted_private_address_when_allowed()
        {
            AllowedDestination[] privateEntry = [Entry("10.0.0.0/8", 8080)];
            Assert.Null(Check("http", "hooks.internal", 8080, ["10.1.2.3"], privateEntry, allowHttp: true));
            Assert.Equal("https_required", Check("http", "hooks.internal", 8080, ["10.1.2.3"], privateEntry, allowHttp: false));
            Assert.Null(Check("https", "hooks.internal", 8080, ["10.1.2.3"], privateEntry, allowHttp: false));
            Assert.Equal("private_address_not_allowlisted:10.1.2.3", Check("http", "hooks.internal", 8080, ["10.1.2.3"], [], allowHttp: true));
            // Public: never plain http, allowlisted or not.
            Assert.Equal("https_required", Check("http", "hooks.example.com", 443, ["8.8.8.8"], allowHttp: true));
            Assert.Equal("port_not_allowed:80", Check("http", "hooks.example.com", 80, ["8.8.8.8"], allowHttp: true));
            Assert.Equal("https_required", Check("http", "hooks.example.com", 80, ["8.8.8.8"], [Entry("hooks.example.com", 80)], allowHttp: true));
            // One public address in the answer: plain http is refused for the whole destination.
            Assert.Equal("https_required", Check("http", "mixed.example.com", 8080, ["10.1.2.3", "8.8.8.8"], [Entry("mixed.example.com", 8080)], allowHttp: true));
        }

        /// <summary>Allowlist entries: ports 1–65535, a CIDR or a DNS host name, and never a network that starts in a blocked range.</summary>
        [Theory]
        [InlineData("10.0.0.0/8", new int[0], true)]
        [InlineData("10.20.0.0/16", new[] { 1, 8443, 65535 }, true)]
        [InlineData("192.168.1.10/32", new[] { 8080 }, true)]
        [InlineData("fd00::/8", new[] { 443 }, true)]
        [InlineData("8.8.8.0/24", new[] { 8443 }, true)]
        [InlineData("hooks.example.com", new[] { 443 }, true)]
        [InlineData("hooks.internal.", new[] { 8443 }, true)]
        [InlineData("10.0.0.0/8", new[] { 0 }, false)]
        [InlineData("10.0.0.0/8", new[] { 65536 }, false)]
        [InlineData("10.0.0.0/8", new[] { -1 }, false)]
        [InlineData("10.0.0.0/33", new int[0], false)]
        [InlineData("not-a-cidr/8", new int[0], false)]
        [InlineData("10.0.0.1", new int[0], false)]
        [InlineData("not a host", new int[0], false)]
        [InlineData("", new int[0], false)]
        [InlineData("127.0.0.0/8", new int[0], false)]
        [InlineData("127.0.0.1/32", new[] { 5280 }, false)]
        [InlineData("169.254.169.254/32", new int[0], false)]
        [InlineData("0.0.0.0/0", new int[0], false)]
        [InlineData("::1/128", new int[0], false)]
        [InlineData("fe80::/10", new int[0], false)]
        [InlineData("64:ff9b::a9fe:a9fe/128", new int[0], false)]
        [InlineData("::/96", new int[0], false)]
        public void Allowlist_entries_are_validated(string destination, int[] ports, bool valid)
        {
            var error = DestinationPolicy.ValidateEntry(Entry(destination, ports));
            Assert.True(valid == (error is null), error ?? "accepted");
        }

        private const string Secret = "whsec_test";
        private const string Id = "0192f0a0-0000-7000-8000-000000000001";
        private const string Body = """{"id":"x","data":{"host":"db-1"}}""";
        private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

        /// <summary>The reference verifier accepts what <see cref="WebhookSigner.Sign"/> produced, and nothing tampered with.</summary>
        [Fact]
        public void The_reference_verifier_accepts_a_signature_and_rejects_any_tampering()
        {
            var timestamp = Now.ToUnixTimeSeconds();
            var signature = WebhookSigner.Sign(Secret, Id, timestamp, Body);
            Assert.StartsWith("v1,", signature);

            Assert.True(WebhookSigner.Verify(Secret, Id, timestamp.ToString(), signature, Body, Now, Tolerance));
            // A receiver may see several signatures (rotation): any one matching is enough.
            Assert.True(WebhookSigner.Verify(Secret, Id, timestamp.ToString(), "v1,c29tZXRoaW5nIGVsc2U= " + signature, Body, Now, Tolerance));

            Assert.False(WebhookSigner.Verify(Secret, Id, timestamp.ToString(), signature, Body + " ", Now, Tolerance));
            Assert.False(WebhookSigner.Verify(Secret, Id, timestamp.ToString(), signature, Body.Replace("db-1", "db-2"), Now, Tolerance));
            Assert.False(WebhookSigner.Verify(Secret, Id[..^1] + "2", timestamp.ToString(), signature, Body, Now, Tolerance));
            Assert.False(WebhookSigner.Verify(Secret, Id, (timestamp + 1).ToString(), signature, Body, Now, Tolerance));
            Assert.False(WebhookSigner.Verify("whsec_other", Id, timestamp.ToString(), signature, Body, Now, Tolerance));
            Assert.False(WebhookSigner.Verify(Secret, Id, timestamp.ToString(), signature[..^2] + "AA", Body, Now, Tolerance));
            Assert.False(WebhookSigner.Verify(Secret, Id, timestamp.ToString(), "", Body, Now, Tolerance));
        }

        /// <summary>A signature outside the tolerance, either way, is refused: a captured request can't be replayed later.</summary>
        [Fact]
        public void A_timestamp_outside_the_tolerance_is_refused()
        {
            var old = Now.AddMinutes(-6).ToUnixTimeSeconds();
            var future = Now.AddMinutes(6).ToUnixTimeSeconds();
            Assert.False(WebhookSigner.Verify(Secret, Id, old.ToString(), WebhookSigner.Sign(Secret, Id, old, Body), Body, Now, Tolerance));
            Assert.False(WebhookSigner.Verify(Secret, Id, future.ToString(), WebhookSigner.Sign(Secret, Id, future, Body), Body, Now, Tolerance));
            var edge = Now.AddMinutes(-5).ToUnixTimeSeconds();
            Assert.True(WebhookSigner.Verify(Secret, Id, edge.ToString(), WebhookSigner.Sign(Secret, Id, edge, Body), Body, Now, Tolerance));
        }

        /// <summary>A timestamp header that isn't a number, or lies outside what a date can hold, returns false instead of throwing.</summary>
        [Theory]
        [InlineData("99999999999999")]
        [InlineData("253402300800")]
        [InlineData("9223372036854775807")]
        [InlineData("-1")]
        [InlineData("-62135596801")]
        [InlineData("99999999999999999999999")]
        [InlineData("1.5")]
        [InlineData("")]
        [InlineData("now")]
        public void A_timestamp_out_of_range_or_malformed_returns_false(string timestamp)
        {
            var signature = long.TryParse(timestamp, out var t) ? WebhookSigner.Sign(Secret, Id, t, Body) : "v1,x";
            Assert.False(WebhookSigner.Verify(Secret, Id, timestamp, signature, Body, Now, Tolerance));
            Assert.False(WebhookSigner.Verify(Secret, Id, timestamp, signature, Body, DateTimeOffset.MaxValue, TimeSpan.MaxValue));
        }
    }
}
