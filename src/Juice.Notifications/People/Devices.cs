using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.People
{
    // Device registrations (design §9.2, phase-2-plan M13): a person's app registers its push token from a signed-in session. Push itself is phase 4;
    // phase 2 keeps the registrations, moves a token to whoever registered it last, and drops the ones not seen for a while.

    public sealed record DeviceView(Guid Id, string Platform, string AppId, string? Locale, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);

    /// <summary>A Web Push subscription as the browser gives it (RFC 8030/8291): the push service's endpoint and the keys to encrypt for it.</summary>
    public sealed record WebPushSubscription(string? Endpoint, string? P256dh, string? Auth);

    /// <summary>Configuration section <c>Devices</c>.</summary>
    public sealed class DeviceOptions
    {
        public const string Section = "Devices";

        /// <summary>Registrations not seen (registered again) for this long are removed (design §9.2: 60 days).</summary>
        public TimeSpan Unseen { get; set; } = TimeSpan.FromDays(60);

        /// <summary>How often the sweep runs; it also removes verification codes a day past their expiry.</summary>
        public TimeSpan SweepInterval { get; set; } = TimeSpan.FromDays(1);
    }

    public static partial class DeviceRules
    {
        public const int MaxPerPerson = 50;
        public const int MaxToken = 4096;

        [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$")]
        internal static partial Regex AppId();

        /// <summary>An FCM registration token: printable ASCII, no spaces.</summary>
        [GeneratedRegex("^[\\x21-\\x7E]{1,4096}$")]
        internal static partial Regex FcmToken();

        /// <summary>An APNs device token: hex.</summary>
        [GeneratedRegex("^[0-9a-fA-F]{64,200}$")]
        internal static partial Regex ApnsToken();

        [GeneratedRegex("^[A-Za-z0-9_-]+={0,2}$")]
        internal static partial Regex Base64Url();
    }

    /// <summary>
    /// A person's device registrations. Not audited: an app registers on every start, and the registration proves itself by coming from the
    /// person's own session (§9.2), so there is no one else's change to record.
    /// </summary>
    public sealed class DeviceRegistry(NotificationsDbContext db, ContactCrypto crypto, TimeProvider clock)
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>
        /// Registers a device for the person, or refreshes it: the same token (for Web Push, the same endpoint) on the same platform and app is one
        /// registration, moved to this person if another had it, so a shared device never pushes to its previous user. A person keeps up to
        /// <see cref="DeviceRules.MaxPerPerson"/>; registering past that drops the ones seen longest ago.
        /// </summary>
        public async Task<Outcome<DeviceView>> RegisterAsync(Guid tenantId, Guid subscriberId, string? platform, string? appId, string? token,
            WebPushSubscription? subscription, string? locale, CancellationToken ct)
        {
            var errors = new List<FieldError>();
            if (platform is null || !DevicePlatforms.All.Contains(platform))
            {
                errors.Add(new FieldError("/platform", $"The platform is one of: {string.Join(", ", DevicePlatforms.All)}."));
            }
            if (appId is null || !DeviceRules.AppId().IsMatch(appId))
            {
                errors.Add(new FieldError("/appId", "An app id is 1–200 letters, digits, '.', '_' or '-', starting with a letter or digit."));
            }
            if (locale is not null && !DirectoryRules.IsLocale(locale))
            {
                errors.Add(new FieldError("/locale", "A locale is a language tag such as en or vi-VN."));
            }
            // What identifies the device (hashed, unique), and what is stored (encrypted).
            string? identity = null, stored = null;
            switch (platform)
            {
                case DevicePlatforms.Fcm when subscription is null && token is not null && DeviceRules.FcmToken().IsMatch(token):
                    identity = stored = token;
                    break;
                case DevicePlatforms.Apns when subscription is null && token is not null && DeviceRules.ApnsToken().IsMatch(token):
                    identity = stored = token.ToLowerInvariant();
                    break;
                case DevicePlatforms.WebPush when token is null && subscription is not null:
                    if (WebPushProblem(subscription) is { } problem)
                    {
                        errors.Add(problem);
                        break;
                    }
                    identity = subscription.Endpoint!;
                    stored = JsonSerializer.Serialize(new WebPushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth), Json);
                    break;
                case DevicePlatforms.WebPush:
                    errors.Add(new FieldError("/subscription", "A webpush device sends its subscription (endpoint, p256dh, auth), not a token."));
                    break;
                case DevicePlatforms.Fcm or DevicePlatforms.Apns:
                    errors.Add(new FieldError("/token", platform == DevicePlatforms.Fcm
                        ? $"An fcm token is 1–{DeviceRules.MaxToken} printable characters without spaces, and no subscription."
                        : "An apns token is 64–200 hexadecimal digits, and no subscription."));
                    break;
            }
            if (errors.Count > 0)
            {
                return Outcome<DeviceView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.", errors);
            }
            var now = clock.UtcNow();
            var hash = crypto.Hash("dev", tenantId.ToString(), platform!, appId!, identity!);
            var token64 = crypto.Protect(stored!, ContactCrypto.DeviceContext(tenantId, platform!, appId!));
            var keyId = crypto.KeyId;
            // One statement: two registrations of one token at once end as one row, with whoever wrote last.
            var row = (await db.Database.SqlQuery<RegisteredRow>($"""
                INSERT INTO notifications.device_registrations AS d (tenant_id, id, subscriber_id, platform, app_id, token, token_hash, key_id, locale, created_at, last_seen_at)
                VALUES ({tenantId}, {Guid.CreateVersion7()}, {subscriberId}, {platform}, {appId}, {token64}, {hash}, {keyId}, {locale}, {now}, {now})
                ON CONFLICT (tenant_id, platform, app_id, token_hash) DO UPDATE
                    SET subscriber_id = excluded.subscriber_id, token = excluded.token, key_id = excluded.key_id,
                        locale = COALESCE(excluded.locale, d.locale), -- a refresh without one keeps the one it had
                        last_seen_at = excluded.last_seen_at
                RETURNING d.id, d.created_at, d.locale
                """).ToListAsync(ct)).Single();
            await db.Database.ExecuteSqlAsync($"""
                DELETE FROM notifications.device_registrations
                WHERE tenant_id = {tenantId} AND id IN (
                    SELECT id FROM notifications.device_registrations WHERE tenant_id = {tenantId} AND subscriber_id = {subscriberId}
                    ORDER BY last_seen_at DESC, id DESC OFFSET {DeviceRules.MaxPerPerson})
                """, ct);
            return Outcome<DeviceView>.Ok(new DeviceView(row.Id, platform!, appId!, row.Locale, row.CreatedAt, now));
        }

        private sealed record RegisteredRow(Guid Id, DateTimeOffset CreatedAt, string? Locale);

        private static FieldError? WebPushProblem(WebPushSubscription s)
        {
            // https only, as the Push API gives it; phase 4's sender checks the destination policy again before it connects.
            if (s.Endpoint is not { Length: > 0 and <= 2000 } endpoint || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || endpoint.Any(char.IsControl))
            {
                return new FieldError("/subscription/endpoint", "The endpoint is the https URL the browser's push service gave, up to 2,000 characters.");
            }
            // An uncompressed P-256 public key is 65 bytes (87 characters unpadded); the auth secret is 16 bytes (22).
            if (s.P256dh is not { Length: >= 80 and <= 100 } key || !DeviceRules.Base64Url().IsMatch(key))
            {
                return new FieldError("/subscription/p256dh", "p256dh is the subscription's public key, base64url.");
            }
            return s.Auth is { Length: >= 16 and <= 64 } auth && DeviceRules.Base64Url().IsMatch(auth)
                ? null
                : new FieldError("/subscription/auth", "auth is the subscription's authentication secret, base64url.");
        }

        /// <summary>The person's registrations, and those of an identity linked into them (as their contact points are).</summary>
        public async Task<IReadOnlyList<DeviceView>> ListAsync(Guid tenantId, Guid subscriberId, CancellationToken ct)
        {
            var self = await DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            return await db.Devices.AsNoTracking().Where(d => d.TenantId == tenantId && self.Contains(d.SubscriberId))
                .OrderByDescending(d => d.LastSeenAt).ThenBy(d => d.Id)
                .Select(d => new DeviceView(d.Id, d.Platform, d.AppId, d.Locale, d.CreatedAt, d.LastSeenAt)).ToListAsync(ct);
        }

        /// <summary>Removes one of the person's registrations (sign-out), theirs or a linked identity's. False when it isn't, or is already gone.</summary>
        public async Task<bool> RemoveAsync(Guid tenantId, Guid subscriberId, Guid id, CancellationToken ct)
        {
            var self = await DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            return await db.Devices.Where(d => d.TenantId == tenantId && d.Id == id && self.Contains(d.SubscriberId)).ExecuteDeleteAsync(ct) > 0;
        }
    }

    /// <summary>
    /// Removes device registrations unseen for <see cref="DeviceOptions.Unseen"/> and verification codes a day past their expiry, on every
    /// replica every <see cref="DeviceOptions.SweepInterval"/> (deleting is safe to repeat), a thousand rows at a time.
    /// </summary>
    public sealed class DeviceSweep(IServiceScopeFactory scopes, IOptions<DeviceOptions> options, TimeProvider clock, ILogger<DeviceSweep> logger) : BackgroundService
    {
        private const int Batch = 1000;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(options.Value.SweepInterval, clock);
            do
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Sweeping old device registrations failed; the next sweep tries again");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        /// <summary>One sweep: how many registrations and codes it removed.</summary>
        public async Task<(int Devices, int Codes)> SweepAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var now = clock.UtcNow();
            var unseen = now - options.Value.Unseen;
            int devices = 0, removed;
            do
            {
                removed = await db.Database.ExecuteSqlAsync($"""
                    DELETE FROM notifications.device_registrations
                    WHERE (tenant_id, id) IN (SELECT tenant_id, id FROM notifications.device_registrations WHERE last_seen_at < {unseen} LIMIT {Batch})
                    """, ct);
                devices += removed;
            }
            while (removed == Batch);
            var codes = await db.ContactVerifications.Where(v => v.ExpiresAt < now - TimeSpan.FromDays(1)).ExecuteDeleteAsync(ct);
            if (devices > 0 || codes > 0)
            {
                logger.LogInformation("Removed {Devices} device registrations unseen since {Unseen} and {Codes} expired verification codes", devices, unseen, codes);
            }
            return (devices, codes);
        }
    }
}
