using System.Net;
using System.Net.Sockets;

namespace Juice.Notifications.Providers
{
    public enum DestinationClass
    {
        /// <summary>Reachable without an allowlist entry.</summary>
        Public,
        /// <summary>Private or internal: only with an allowlist entry.</summary>
        Private,
        /// <summary>Never reachable, allowlisted or not: loopback, link-local (cloud metadata), unspecified, multicast, reserved.</summary>
        Blocked,
    }

    /// <summary>An allowlist entry: a CIDR, or an exact host name, with its ports.</summary>
    public sealed record AllowedDestination(string Destination, IReadOnlyList<int> Ports)
    {
        public bool IsCidr => Destination.Contains('/');

        public bool Covers(string host, IPAddress address, int port)
        {
            if (!(Ports.Count == 0 ? port == 443 : Ports.Contains(port)))
            {
                return false;
            }
            // A trailing dot (the fully qualified spelling) names the same host, on either side.
            return IsCidr
                ? IPNetwork.TryParse(Destination, out var network) && network.Contains(DestinationPolicy.Normalize(address))
                : string.Equals(Destination.TrimEnd('.'), host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// SSRF rules for every outbound HTTP call (design §13.1, D16), ported from Juice.Observability.Webhooks with its tests. Every address a host resolves to is checked on every new
    /// connection; one blocked address rejects the destination, so DNS that mixes public and internal answers cannot
    /// be used to reach inside. Loopback, link-local and cloud metadata stay blocked even when an allowlist entry
    /// covers them.
    /// </summary>
    public static class DestinationPolicy
    {
        private static readonly IPNetwork[] Blocked =
        [
            IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"),
            IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("198.51.100.0/24"), IPNetwork.Parse("203.0.113.0/24"),
            IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4"),
            IPNetwork.Parse("::/128"), IPNetwork.Parse("::1/128"), IPNetwork.Parse("fe80::/10"), IPNetwork.Parse("ff00::/8"),
            IPNetwork.Parse("fd00:ec2::254/128"), IPNetwork.Parse("2001:db8::/32"),
        ];

        private static readonly IPNetwork[] Private =
        [
            IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.168.0.0/16"),
            IPNetwork.Parse("100.64.0.0/10"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("192.0.0.0/24"),
            IPNetwork.Parse("fc00::/7"), IPNetwork.Parse("64:ff9b:1::/48"),
        ];

        /// <summary>IPv4-mapped IPv6 (<c>::ffff:127.0.0.1</c>) is judged as the IPv4 address it carries.</summary>
        public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");
        private static readonly IPNetwork Nat64Local = IPNetwork.Parse("64:ff9b:1::/48");
        private static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");
        private static readonly IPNetwork Ipv4Compatible = IPNetwork.Parse("::/96");

        /// <summary>
        /// An address's class. IPv6 forms that carry an IPv4 address are judged as strictly as that address too: a NAT64 gateway turns
        /// <c>64:ff9b::a9fe:a9fe</c> into 169.254.169.254 (cloud metadata), and 6to4 (<c>2002::/16</c>) relays to the IPv4 it embeds. The
        /// deprecated IPv4-compatible form (<c>::a.b.c.d</c>) is never reachable.
        /// </summary>
        public static DestinationClass Classify(IPAddress address)
        {
            var ip = Normalize(address);
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = ip.GetAddressBytes();
                if (Ipv4Compatible.Contains(ip))
                {
                    return DestinationClass.Blocked;
                }
                if (Nat64.Contains(ip))
                {
                    return Stricter(DestinationClass.Public, ClassifyOwn(new IPAddress(bytes[12..16])));
                }
                if (Nat64Local.Contains(ip))
                {
                    // A local-use prefix can be /48, /56, /64 or /96 (RFC 6052 §2.2), which puts the IPv4 at a different place, and the rest is the
                    // sender's choice: judged at every place, strictest wins.
                    return Rfc6052Positions.Select(p => ClassifyOwn(new IPAddress(p.Select(i => bytes[i]).ToArray())))
                        .Aggregate(DestinationClass.Private, Stricter);
                }
                if (SixToFour.Contains(ip))
                {
                    return Stricter(DestinationClass.Public, ClassifyOwn(new IPAddress(bytes[2..6])));
                }
            }
            return ClassifyOwn(ip);
        }

        /// <summary>Where the IPv4 sits for each RFC 6052 prefix length that fits in 64:ff9b:1::/48 (byte 8, the u octet, is skipped).</summary>
        private static readonly int[][] Rfc6052Positions = [[6, 7, 9, 10], [7, 9, 10, 11], [9, 10, 11, 12], [12, 13, 14, 15]];

        private static DestinationClass Stricter(DestinationClass a, DestinationClass b) => (DestinationClass)Math.Max((int)a, (int)b);

        private static DestinationClass ClassifyOwn(IPAddress ip)
        {
            if (ip.Equals(IPAddress.Broadcast) || Blocked.Any(n => n.Contains(ip)))
            {
                return DestinationClass.Blocked;
            }
            return Private.Any(n => n.Contains(ip)) ? DestinationClass.Private : DestinationClass.Public;
        }

        /// <summary>Null when the connection may proceed; otherwise why it may not.</summary>
        public static string? Check(string scheme, string host, int port, IReadOnlyList<IPAddress> addresses,
            IReadOnlyList<AllowedDestination> allowlist, bool allowHttpToAllowlisted)
        {
            if (addresses.Count == 0)
            {
                return "no_address";
            }
            foreach (var address in addresses)
            {
                var entry = allowlist.FirstOrDefault(a => a.Covers(host, address, port));
                switch (Classify(address))
                {
                    case DestinationClass.Blocked:
                        return $"blocked_address:{Normalize(address)}";
                    case DestinationClass.Private when entry is null:
                        return $"private_address_not_allowlisted:{Normalize(address)}";
                    // A public address too: 443 only, unless an allowlist entry names this host or address with the port (§13.1).
                    case DestinationClass.Public when entry is null && port != 443:
                        return $"port_not_allowed:{port}";
                }
                // Plain HTTP only to an allowlisted private destination, and only when the deployment allows it at all.
                if (scheme != Uri.UriSchemeHttps && !(scheme == Uri.UriSchemeHttp && allowHttpToAllowlisted && entry is not null && Classify(address) == DestinationClass.Private))
                {
                    return "https_required";
                }
            }
            return null;
        }

        /// <summary>Configuration check: an allowlist entry that only covers never-reachable addresses is a mistake.</summary>
        public static string? ValidateEntry(AllowedDestination entry)
        {
            if (entry.Ports.Any(p => p is < 1 or > 65535))
            {
                return $"allowlist '{entry.Destination}': ports are 1 to 65535";
            }
            if (!entry.IsCidr)
            {
                return Uri.CheckHostName(entry.Destination) == UriHostNameType.Dns ? null : $"allowlist '{entry.Destination}': not a CIDR or host name";
            }
            if (!IPNetwork.TryParse(entry.Destination, out var network))
            {
                return $"allowlist '{entry.Destination}': not a valid CIDR";
            }
            // Refused when it can only ever cover blocked addresses: a single one that is, or a network inside a blocked range. A wider network
            // (0.0.0.0/0, a /64 inside 64:ff9b:1::/48) isn't judged by its base address; its blocked parts stay blocked at send time.
            var single = network.PrefixLength == (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            return (single ? Classify(network.BaseAddress) == DestinationClass.Blocked
                    : WhollyBlocked(network) || (Embedded(network) is { } v4 && WhollyBlocked(v4)))
                ? $"allowlist '{entry.Destination}': loopback, link-local, metadata and reserved addresses are never reachable"
                : null;
        }

        private static bool WhollyBlocked(IPNetwork network)
            => Blocked.Append(Ipv4Compatible).Any(b => b.PrefixLength <= network.PrefixLength
                && b.BaseAddress.AddressFamily == network.BaseAddress.AddressFamily && b.Contains(network.BaseAddress));


        /// <summary>
        /// The IPv4 network an IPv6 network carries, when it fixes all of the IPv4 prefix: inside 64:ff9b::/96 or ::ffff:0:0/96 (bytes 12–15), or
        /// 6to4 2002::/16 (bytes 2–5). Null otherwise.
        /// </summary>
        private static IPNetwork? Embedded(IPNetwork network)
        {
            var bytes = network.BaseAddress.GetAddressBytes();
            // IPv4-mapped by its bytes (::ffff:0:0/96): IPNetwork.Contains doesn't match mapped addresses as plain IPv6.
            var mapped = bytes.Length == 16 && bytes[..10].All(b => b == 0) && bytes[10] == 0xff && bytes[11] == 0xff;
            if (network.PrefixLength >= 96 && (Nat64.Contains(network.BaseAddress) || mapped))
            {
                return new IPNetwork(new IPAddress(bytes[12..16]), network.PrefixLength - 96);
            }
            if (network.PrefixLength >= 16 && SixToFour.Contains(network.BaseAddress))
            {
                return new IPNetwork(new IPAddress(bytes[2..6]), Math.Min(32, network.PrefixLength - 16));
            }
            return null;
        }

        /// <summary>A socket to the first address of <paramref name="addresses"/> that accepts, after they all passed <see cref="Check"/>.</summary>
        internal static async Task<Socket> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
        {
            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                    return socket;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    last = ex;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            throw last ?? new SocketException((int)SocketError.HostNotFound);
        }
    }

    /// <summary>The destination failed <see cref="DestinationPolicy.Check"/>; never retried.</summary>
    public sealed class DestinationBlockedException(string reason) : Exception($"Destination blocked: {reason}")
    {
        public string Reason { get; } = reason;
    }
}
