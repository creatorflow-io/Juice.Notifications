using System.Security.Cryptography;
using Juice.Notifications.People;
using Juice.Notifications.Providers;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Tests
{
    /// <summary>Contact address encryption (phase-2-plan I19) and the SMTP refusals that fail a contact point (M13).</summary>
    public sealed class ContactCryptoTests
    {
        private const string Current = "NOTIFICATIONS_TEST_CRYPTO_CURRENT";
        private const string Old = "NOTIFICATIONS_TEST_CRYPTO_OLD";
        private const string HashKey = "NOTIFICATIONS_TEST_CRYPTO_HASH";
        private const string Short = "NOTIFICATIONS_TEST_CRYPTO_SHORT";

        static ContactCryptoTests()
        {
            Environment.SetEnvironmentVariable(Current, Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()));
            Environment.SetEnvironmentVariable(Old, Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray()));
            Environment.SetEnvironmentVariable(HashKey, Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray()));
            Environment.SetEnvironmentVariable(Short, Convert.ToBase64String(new byte[16]));
        }

        private static ContactCrypto Crypto(string keyId = "k2", string keyRef = "env:" + Current, Dictionary<string, string>? previous = null)
            => new(Options.Create(new EncryptionOptions
            {
                KeyRef = keyRef, KeyId = keyId, HashKeyRef = "env:" + HashKey, PreviousKeyRefs = previous ?? new() { ["k1"] = "env:" + Old },
            }));

        private static readonly string Context = ContactCrypto.ContactContext(Guid.Empty, Guid.Empty, "email");

        [Fact]
        public void An_address_round_trips_and_differs_each_time()
        {
            var crypto = Crypto();
            var first = crypto.Protect("pat@example.com", Context);
            var second = crypto.Protect("pat@example.com", Context);

            Assert.NotEqual(first, second);
            Assert.DoesNotContain("example", first);
            Assert.Equal("pat@example.com", crypto.Unprotect(first, "k2", Context));
            Assert.Equal("k2", crypto.KeyId);
        }

        [Fact]
        public void A_value_moved_to_another_row_or_changed_doesnt_decrypt()
        {
            var crypto = Crypto();
            var value = crypto.Protect("pat@example.com", Context);
            var elsewhere = ContactCrypto.ContactContext(Guid.Empty, Guid.NewGuid(), "email");

            Assert.ThrowsAny<CryptographicException>(() => crypto.Unprotect(value, "k2", elsewhere));
            var bytes = Convert.FromBase64String(value);
            bytes[^1] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() => crypto.Unprotect(Convert.ToBase64String(bytes), "k2", Context));
            Assert.ThrowsAny<CryptographicException>(() => crypto.Unprotect(Convert.ToBase64String(new byte[10]), "k2", Context));
        }

        [Fact]
        public void A_rotated_key_still_reads_its_values_and_a_dropped_one_says_so()
        {
            var before = Crypto(keyId: "k1", keyRef: "env:" + Old, previous: []);
            var value = before.Protect("pat@example.com", Context);

            Assert.Equal("pat@example.com", Crypto().Unprotect(value, "k1", Context));
            var dropped = Crypto(previous: []);
            Assert.Equal("The encryption key 'k1' isn't configured.", Assert.Throws<ContactKeyMissingException>(() => dropped.Unprotect(value, "k1", Context)).Message);
        }

        [Fact]
        public void Hashes_are_stable_across_key_rotation_and_tell_tenants_and_channels_apart()
        {
            var tenant = Guid.NewGuid();
            var hash = Crypto().ContactHash(tenant, "email", "pat@example.com");

            Assert.Equal(hash, Crypto(keyId: "k1", keyRef: "env:" + Old, previous: []).ContactHash(tenant, "email", "pat@example.com"));
            Assert.Equal(64, hash.Length);
            Assert.NotEqual(hash, Crypto().ContactHash(Guid.NewGuid(), "email", "pat@example.com"));
            Assert.NotEqual(hash, Crypto().ContactHash(tenant, "sms", "pat@example.com"));
            // Parts are separated, so moving a boundary changes the hash.
            Assert.NotEqual(Crypto().Hash("a", "bc"), Crypto().Hash("ab", "c"));
        }

        [Theory]
        [InlineData("pat@example.com", "p…@example.com")]
        [InlineData("+15550100", "…0100")]
        [InlineData("abc", "…")]
        public void An_address_is_masked_for_history(string address, string masked) => Assert.Equal(masked, ContactCrypto.Mask(address));

        [Fact]
        public void A_mask_doesnt_split_a_character_outside_the_bmp()
        {
            var face = char.ConvertFromUtf32(0x1F600);
            Assert.Equal(face + "…@example.com", ContactCrypto.Mask(face + "x@example.com"));
            Assert.Equal("…" + face + "123", ContactCrypto.Mask("abcdef" + face + "123"));
        }

        [Fact]
        public void A_key_that_couldnt_be_read_is_read_again_next_time()
        {
            const string late = "NOTIFICATIONS_TEST_CRYPTO_LATE";
            Environment.SetEnvironmentVariable(late, null);
            var crypto = Crypto(keyRef: "env:" + late);
            Assert.Throws<InvalidOperationException>(crypto.Check);
            Environment.SetEnvironmentVariable(late, Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            crypto.Check();
            Assert.Equal("x", crypto.Unprotect(crypto.Protect("x", Context), "k2", Context));
        }

        [Fact]
        public void Missing_or_malformed_keys_stop_the_host()
        {
            Assert.Contains("required", Assert.Throws<InvalidOperationException>(() => new ContactCrypto(Options.Create(new EncryptionOptions { KeyRef = null })).Check()).Message);
            Assert.Contains("is 16 bytes", Assert.Throws<InvalidOperationException>(() => Crypto(keyRef: "env:" + Short).Check()).Message);
            Assert.Contains("is not set", Assert.Throws<InvalidOperationException>(() => Crypto(keyRef: "env:NOTIFICATIONS_TEST_CRYPTO_UNSET").Check()).Message);
            Assert.Contains("names the current key id", Assert.Throws<InvalidOperationException>(() => Crypto(previous: new() { ["k2"] = "env:" + Old }).Check()).Message);
            Assert.Contains("KeyId", Assert.Throws<InvalidOperationException>(() => Crypto(keyId: "K 1").Check()).Message);
            Crypto().Check();
        }

        [Theory]
        [InlineData("5.1.1 <pat@example.com>: Recipient address rejected: User unknown", true)]
        [InlineData("5.1.10 Recipient address rejected", true)]
        [InlineData(" 5.1.6 Mailbox has moved", true)]
        // DNS trouble or a relay's configuration says these, not the mailbox.
        [InlineData("5.1.2 Bad destination system", false)]
        [InlineData("5.1.0 Other address status", false)]
        [InlineData("5.2.2 Mailbox full", false)]
        [InlineData("5.7.1 Relaying denied", false)]
        [InlineData("4.1.1 Try later", false)]
        [InlineData("User unknown 5.1.1", false)]
        [InlineData("5.1.1000 x", false)]
        public void Only_an_addressing_status_says_the_address_is_gone(string reply, bool gone) => Assert.Equal(gone, SmtpProvider.AddressStatus().IsMatch(reply));

        [Theory]
        [InlineData("550 5.1.1 <PAT@Example.com>: unknown", "pat@example.com", "550 5.1.1 <p…@example.com>: unknown")]
        [InlineData("550 5.1.1 <jane@xn--bcher-kva.de> unknown", "jane@bücher.de", "550 5.1.1 <j…@bücher.de> unknown")]
        [InlineData("550 <jane@bücher.de> unknown", "jane@xn--bcher-kva.de", "550 <j…@xn--bcher-kva.de> unknown")]
        [InlineData("no address here", "pat@example.com", "no address here")]
        public void An_address_is_scrubbed_from_a_reply_in_either_spelling_of_its_domain(string reply, string address, string scrubbed)
            => Assert.Equal(scrubbed, Masking.Scrub(reply, address, ContactCrypto.Mask(address)));
    }
}
