using System.Security.Cryptography;
using System.Text;
using Juice.Notifications.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.People
{
    /// <summary>
    /// Configuration section <c>Encryption</c> (phase-2-plan I19): the keys contact addresses and device tokens are kept under, as secret references
    /// (design D15), each a base64 32-byte key. Required: the service doesn't start without both. By default the environment variables
    /// <c>NOTIFICATIONS_CONTACT_KEY</c> and <c>NOTIFICATIONS_CONTACT_HASH_KEY</c>.
    /// </summary>
    public sealed partial class EncryptionOptions
    {
        public const string Section = "Encryption";

        /// <summary>The key new values are encrypted with.</summary>
        public string? KeyRef { get; set; } = "env:NOTIFICATIONS_CONTACT_KEY";

        /// <summary>The id stored with each value encrypted under <see cref="KeyRef"/>: 1–20 lowercase letters, digits or hyphens.</summary>
        public string KeyId { get; set; } = "k1";

        /// <summary>
        /// Keys rotated out, by id, still read for values encrypted under them. A value whose key is gone can't be read: its contact point is
        /// skipped and the delivery fails with the reason; nothing else does.
        /// </summary>
        public Dictionary<string, string> PreviousKeyRefs { get; set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The HMAC key behind address and token hashes and verification codes. Never rotated: the hashes are the unique keys, and a new key would
        /// let the same address be added twice.
        /// </summary>
        public string? HashKeyRef { get; set; } = "env:NOTIFICATIONS_CONTACT_HASH_KEY";

        [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9-]{1,20}$")]
        private static partial System.Text.RegularExpressions.Regex KeyIdPattern();

        /// <summary>The problem with these options, or null when they're fine (checked at start).</summary>
        public string? Problem()
        {
            if (KeyRef is null || !SecretReferences.IsLocal(KeyRef) || HashKeyRef is null || !SecretReferences.IsLocal(HashKeyRef))
            {
                return "Encryption:KeyRef and Encryption:HashKeyRef are required secret references (env:NAME or file:/path) to base64 32-byte keys.";
            }
            if (!KeyIdPattern().IsMatch(KeyId))
            {
                return "Encryption:KeyId is 1–20 lowercase letters, digits or hyphens.";
            }
            if (PreviousKeyRefs.ContainsKey(KeyId))
            {
                return $"Encryption:PreviousKeyRefs names the current key id {KeyId}.";
            }
            return PreviousKeyRefs.FirstOrDefault(p => !KeyIdPattern().IsMatch(p.Key) || !SecretReferences.IsLocal(p.Value))
                is { Key: { } bad } ? $"Encryption:PreviousKeyRefs:{bad} is a key id with a secret reference." : null;
        }
    }

    public static class ContactEncryptionRegistration
    {
        /// <summary>
        /// The encryption service, its keys read on first use (<c>Encryption</c> options, which the host binds). The stages that read contact
        /// points register it; the host checks the keys at start (<see cref="ServiceTypesProvisioner"/>).
        /// </summary>
        public static IServiceCollection AddContactEncryption(this IServiceCollection services)
        {
            services.AddOptions<EncryptionOptions>();
            services.TryAddSingleton<ContactCrypto>();
            return services;
        }
    }

    /// <summary>A value encrypted under a key this deployment no longer has (rotated out and dropped from <c>PreviousKeyRefs</c>).</summary>
    public sealed class ContactKeyMissingException(string keyId) : Exception($"The encryption key '{keyId}' isn't configured.");

    /// <summary>
    /// Encrypts contact addresses and device tokens at rest (design §13.2, phase-2-plan I19), and hashes them for lookups. AES-256-GCM with a random
    /// nonce; the associated data binds a value to where it is stored (its tenant, person and channel), so a ciphertext copied to another row
    /// doesn't decrypt there.
    /// </summary>
    public sealed class ContactCrypto
    {
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private readonly Lazy<(string Id, byte[] Key, IReadOnlyDictionary<string, byte[]> All, byte[] Hash)> _keys;

        // PublicationOnly: a read that fails (a key file not mounted yet) isn't cached; the next use reads again.
        public ContactCrypto(IOptions<EncryptionOptions> options)
            => _keys = new(() =>
            {
                var o = options.Value;
                if (o.Problem() is { } problem)
                {
                    throw new InvalidOperationException(problem);
                }
                var all = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [o.KeyId] = Key(o.KeyRef!, "Encryption:KeyRef") };
                foreach (var (id, reference) in o.PreviousKeyRefs)
                {
                    all[id] = Key(reference, $"Encryption:PreviousKeyRefs:{id}");
                }
                return (o.KeyId, all[o.KeyId], all, Key(o.HashKeyRef!, "Encryption:HashKeyRef"));
            }, LazyThreadSafetyMode.PublicationOnly);

        private static byte[] Key(string reference, string name)
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(SecretReferences.Resolve(reference));
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"{name} isn't base64.");
            }
            return key.Length == 32 ? key : throw new InvalidOperationException($"{name} is {key.Length} bytes; a key is 32.");
        }

        /// <summary>Reads every configured key, so a missing or malformed one stops the host at start rather than the first send.</summary>
        public void Check() => _ = _keys.Value;

        /// <summary>The id of the key new values are encrypted with.</summary>
        public string KeyId => _keys.Value.Id;

        /// <summary>The value encrypted under the current key, bound to <paramref name="context"/>: nonce, tag and ciphertext, base64.</summary>
        public string Protect(string value, string context)
        {
            var plain = Encoding.UTF8.GetBytes(value);
            var output = new byte[NonceSize + TagSize + plain.Length];
            var nonce = output.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(_keys.Value.Key, TagSize);
            aes.Encrypt(nonce, plain, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(context));
            return Convert.ToBase64String(output);
        }

        /// <summary>
        /// The value <see cref="Protect"/> made under <paramref name="keyId"/> for the same <paramref name="context"/>. Throws
        /// <see cref="ContactKeyMissingException"/> when the key is gone, and <see cref="CryptographicException"/> when the value doesn't decrypt.
        /// </summary>
        public string Unprotect(string protectedValue, string keyId, string context)
        {
            if (!_keys.Value.All.TryGetValue(keyId, out var key))
            {
                throw new ContactKeyMissingException(keyId);
            }
            var input = Convert.FromBase64String(protectedValue);
            if (input.Length < NonceSize + TagSize)
            {
                throw new CryptographicException("The value is too short to have been encrypted here.");
            }
            var plain = new byte[input.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plain, Encoding.UTF8.GetBytes(context));
            return Encoding.UTF8.GetString(plain);
        }

        /// <summary>HMAC-SHA256 of the parts, lowercase hex: the same parts always hash the same, under the never-rotated hash key.</summary>
        public string Hash(params string[] parts)
            => Convert.ToHexStringLower(HMACSHA256.HashData(_keys.Value.Hash, Encoding.UTF8.GetBytes(string.Join('\n', parts))));

        /// <summary>Where a contact point's address is bound: its tenant, person and channel.</summary>
        public static string ContactContext(Guid tenantId, Guid subscriberId, string channel) => $"cp\n{tenantId}\n{subscriberId}\n{channel}";

        /// <summary>A contact point's address, encrypted for where it is stored.</summary>
        public string ProtectAddress(Guid tenantId, Guid subscriberId, string channel, string address)
            => Protect(address, ContactContext(tenantId, subscriberId, channel));

        /// <summary>A contact point's address, decrypted. Throws as <see cref="Unprotect"/> does.</summary>
        public string UnprotectAddress(Data.ContactPoint point) => Unprotect(point.Address, point.KeyId, ContactContext(point.TenantId, point.SubscriberId, point.Channel));

        /// <summary>A contact point's address, or null when its key is gone or it doesn't decrypt (a damaged or moved value).</summary>
        public string? TryUnprotectAddress(Data.ContactPoint point)
        {
            try
            {
                return UnprotectAddress(point);
            }
            catch (Exception ex) when (ex is ContactKeyMissingException or CryptographicException or FormatException)
            {
                return null;
            }
        }

        /// <summary>The hash a contact point is unique by and found by: its tenant, channel and normalized address.</summary>
        public string ContactHash(Guid tenantId, string channel, string address) => Hash("cp", tenantId.ToString(), channel, address);

        /// <summary>Where a device token is bound: its tenant, platform and app (not its person: registering it again moves it).</summary>
        public static string DeviceContext(Guid tenantId, string platform, string appId) => $"dev\n{tenantId}\n{platform}\n{appId}";

        /// <summary>
        /// An address shown in history without being it: the first character of the local part and the domain (<c>j…@example.com</c>), or the
        /// last four characters of anything else.
        /// </summary>
        public static string Mask(string address)
        {
            // By runes, so a character outside the BMP isn't cut in half.
            var at = address.LastIndexOf('@');
            if (at > 0)
            {
                return address.EnumerateRunes().First() + "…" + address[at..];
            }
            var runes = address.EnumerateRunes().ToList();
            return runes.Count <= 4 ? "…" : "…" + string.Concat(runes.Skip(runes.Count - 4));
        }
    }
}

namespace Juice.Notifications.People
{
    /// <summary>Keeps a person's address out of text a provider returns (an error, a reply), in the forms a server may quote it.</summary>
    public static class Masking
    {
        private static readonly System.Globalization.IdnMapping Idn = new();

        /// <summary>
        /// <paramref name="text"/> with <paramref name="address"/> replaced by <paramref name="masked"/>, ignoring case, as stored and with its
        /// domain in the other IDN spelling (<c>bücher.de</c> and <c>xn--bcher-kva.de</c>). Other rewrites a server might make aren't caught
        /// (deferred #34).
        /// </summary>
        public static string Scrub(string text, string address, string masked)
        {
            foreach (var form in Forms(address))
            {
                text = text.Replace(form, masked, StringComparison.OrdinalIgnoreCase);
            }
            return text;
        }

        private static IEnumerable<string> Forms(string address)
        {
            yield return address;
            var at = address.LastIndexOf('@');
            if (at <= 0 || at == address.Length - 1)
            {
                yield break;
            }
            var (local, domain) = (address[..at], address[(at + 1)..]);
            string? ascii = null, unicode = null;
            try
            {
                ascii = Idn.GetAscii(domain);
                unicode = Idn.GetUnicode(ascii);
            }
            catch (ArgumentException)
            {
                // Not a domain IDN can spell another way.
            }
            foreach (var other in new[] { ascii, unicode })
            {
                if (other is not null && !string.Equals(other, domain, StringComparison.OrdinalIgnoreCase))
                {
                    yield return local + "@" + other;
                }
            }
        }
    }
}
