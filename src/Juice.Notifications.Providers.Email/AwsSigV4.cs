using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Juice.Notifications.Providers.Email
{
    /// <summary>
    /// AWS Signature Version 4 (the request signing every AWS API takes), enough for SES: the signed headers given, the payload hashed, the query
    /// already encoded. Checked against AWS's own worked example (<c>AwsSigV4Tests</c>).
    /// </summary>
    internal static class AwsSigV4
    {
        public const string DateHeader = "x-amz-date";

        /// <summary>
        /// The <c>Authorization</c> header for a request. <paramref name="headers"/> are the headers to sign (lower-case names; <c>host</c> and
        /// <see cref="DateHeader"/> among them), with the values the request sends.
        /// </summary>
        public static string Authorization(string method, Uri url, IReadOnlyDictionary<string, string> headers, byte[] payload, string accessKey,
            string secretKey, string region, string service, DateTimeOffset now)
        {
            var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var signed = headers.Keys.Select(k => k.ToLowerInvariant()).Order(StringComparer.Ordinal).ToList();
            var canonicalHeaders = string.Concat(signed.Select(k => k + ":" + string.Join(' ',
                headers.First(h => h.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)) + "\n"));
            var signedHeaders = string.Join(';', signed);
            var query = url.Query.TrimStart('?');
            var canonicalQuery = query.Length == 0 ? "" : string.Join('&', query.Split('&').Order(StringComparer.Ordinal));
            var path = url.AbsolutePath.Length == 0 ? "/" : url.AbsolutePath;
            var canonicalRequest = $"{method}\n{path}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{Hex(SHA256.HashData(payload))}";
            var scope = $"{date}/{region}/{service}/aws4_request";
            var toSign = $"AWS4-HMAC-SHA256\n{stamp}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";
            var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), service), "aws4_request");
            return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={Hex(Hmac(key, toSign))}";
        }

        public static string Stamp(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

        private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
    }
}
