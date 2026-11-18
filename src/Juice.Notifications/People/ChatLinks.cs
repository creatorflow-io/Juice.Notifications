using System.Security.Cryptography;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.People
{
    public sealed record IssuedLink(string Channel, string Code, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Linking a person's chat account (phase-4-plan I41, D23): the person asks for a code in the inbox, sends it to the tenant's bot from their
    /// account (a direct message to the Slack app, <c>/start CODE</c> in Telegram), and the bot's callback makes that account their verified
    /// contact point on the channel. The account is never entered by hand nor matched by name or email: only the account the code came from is
    /// linked.
    /// </summary>
    public sealed class ChatLinks(NotificationsDbContext db, ContactCrypto crypto, IChannelRegistry channels, TimeProvider clock, ILogger<ChatLinks> logger)
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
        public const int PerHour = 10;
        public const string LinkedBy = "link";

        /// <summary>Crockford-like: no 0/O, 1/I/L, U, so a code read aloud or typed on a phone survives.</summary>
        private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
        private const int Length = 8;

        /// <summary>A code as typed: letters and digits, any case, with spaces or hyphens between; null when it can't be one.</summary>
        public static string? Normalize(string? text)
        {
            if (text is null || text.Length > 40)
            {
                return null;
            }
            var code = string.Concat(text.Where(c => c is not (' ' or '-')).Select(char.ToUpperInvariant));
            return code.Length == Length && code.All(c => Alphabet.Contains(c)) ? code : null;
        }

        /// <summary>The code as shown: <c>ABCD-EFGH</c>.</summary>
        public static string Show(string code) => code[..4] + "-" + code[4..];

        private string Hash(Guid tenantId, string channel, string code) => crypto.Hash("link", tenantId.ToString(), channel, code);

        /// <summary>
        /// A new code for the person on a channel whose contact points are linked accounts, replacing their outstanding one there. Refused when the
        /// tenant has no enabled integration on the channel (no bot to send it to) and past <see cref="PerHour"/> codes an hour.
        /// </summary>
        public async Task<Outcome<IssuedLink>> IssueAsync(Guid tenantId, Guid subscriberId, string? channelKey, CancellationToken ct)
        {
            var channel = channelKey is null ? null : channels.Find(channelKey);
            if (channel is not { Address: AddressKind.Account })
            {
                var linked = string.Join(", ", channels.All.Where(c => c.Address == AddressKind.Account).Select(c => c.Key).Order(StringComparer.Ordinal));
                return Outcome<IssuedLink>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/channel", linked.Length > 0 ? $"Accounts are linked on: {linked}." : "No channel here links accounts.")]);
            }
            if (!await db.Integrations.AnyAsync(i => i.TenantId == tenantId && i.Channel == channel.Key && i.Enabled, ct))
            {
                return Outcome<IssuedLink>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/channel", $"This tenant has no {channel.Key} integration to link through.")]);
            }
            var now = clock.GetUtcNow();
            if (await db.LinkCodes.CountAsync(l => l.TenantId == tenantId && l.SubscriberId == subscriberId && l.CreatedAt > now.AddHours(-1), ct) >= PerHour)
            {
                return Outcome<IssuedLink>.Conflict(ErrorCodes.TooManyRequests, $"At most {PerHour} link codes an hour; try again later.");
            }
            await db.LinkCodes.Where(l => l.TenantId == tenantId && l.SubscriberId == subscriberId && l.Channel == channel.Key && l.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, now), ct);
            var code = string.Concat(Enumerable.Range(0, Length).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]));
            var link = new LinkCode
            {
                TenantId = tenantId, SubscriberId = subscriberId, Channel = channel.Key, CodeHash = Hash(tenantId, channel.Key, code), CreatedAt = now,
                ExpiresAt = now + Lifetime,
            };
            db.LinkCodes.Add(link);
            await db.SaveChangesAsync(ct);
            return Outcome<IssuedLink>.Ok(new IssuedLink(channel.Key, Show(code), link.ExpiresAt));
        }

        /// <summary>
        /// The bot heard <paramref name="text"/> from <paramref name="account"/>: when it is an outstanding code of this tenant and channel, the
        /// account becomes the code's person's verified contact point (verified anew, and working again, when they had it), and the code is used.
        /// Within the caller's transaction. False when it isn't a code that links.
        /// </summary>
        public async Task<bool> RedeemAsync(Guid tenantId, string channel, string? text, string? account, CancellationToken ct)
        {
            if (Normalize(text) is not { } code || account is not { Length: > 0 and <= 200 })
            {
                return false;
            }
            var now = clock.GetUtcNow();
            var hash = Hash(tenantId, channel, code);
            var link = await db.LinkCodes.SingleOrDefaultAsync(l => l.TenantId == tenantId && l.CodeHash == hash && l.Channel == channel, ct);
            if (link is null || link.UsedAt is not null || link.ExpiresAt <= now)
            {
                return false;
            }
            var subscriber = link.SubscriberId;
            var addressHash = crypto.ContactHash(tenantId, channel, account);
            var id = Guid.CreateVersion7();
            var address = crypto.ProtectAddress(tenantId, subscriber, channel, account);
            var masked = ContactCrypto.Mask(account);
            var mine = await db.ContactPoints.Where(c => c.TenantId == tenantId && c.SubscriberId == subscriber).Select(c => c.AddressHash).ToListAsync(ct);
            if (!mine.Contains(addressHash) && mine.Count >= ContactRules.MaxPerPerson)
            {
                logger.LogWarning("Subscriber {Subscriber} in tenant {Tenant} has {Max} contact points, so a {Channel} account wasn't linked.", subscriber, tenantId,
                    ContactRules.MaxPerPerson, channel);
                return false;
            }
            var ok = ContactHealth.Ok.ToString();
            // Not a tracked change: the person's own act, made through the bot, with no one signed in to audit it as (as a source's contacts).
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.contact_points AS c (tenant_id, id, subscriber_id, channel, address, address_hash, key_id, masked, verified_at, verified_by, health, created_at)
                VALUES ({tenantId}, {id}, {subscriber}, {channel}, {address}, {addressHash}, {crypto.KeyId}, {masked}, {now}, {LinkedBy}, {ok}, {now})
                ON CONFLICT (tenant_id, subscriber_id, channel, address_hash)
                DO UPDATE SET verified_at = {now}, verified_by = {LinkedBy}, health = {ok}, health_reason = NULL, health_changed_at = {now}
                """, ct);
            var pointId = await db.ContactPoints.Where(c => c.TenantId == tenantId && c.SubscriberId == subscriber && c.Channel == channel && c.AddressHash == addressHash)
                .Select(c => c.Id).SingleAsync(ct);
            link.UsedAt = now;
            link.ContactPointId = pointId;
            logger.LogInformation("Subscriber {Subscriber} in tenant {Tenant} linked a {Channel} account as contact point {ContactPoint}.", subscriber, tenantId,
                channel, pointId);
            return true;
        }
    }
}
