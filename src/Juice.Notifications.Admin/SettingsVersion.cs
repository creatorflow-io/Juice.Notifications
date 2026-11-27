namespace Juice.Notifications.Api
{
    /// <summary>
    /// The version a tenant-settings PUT expects (M52): from <c>If-Match</c> (<c>"n"</c> or <c>W/"n"</c>; <c>*</c> or nothing counts as omitted) or the
    /// body's <c>version</c>. Both given and different, or an <c>If-Match</c> that isn't one tag of a version, is refused.
    /// </summary>
    internal static class SettingsVersion
    {
        /// <summary>The entity tag of a settings version.</summary>
        public static string ETag(int version) => "\"" + version + "\"";

        /// <summary>Parses an <c>If-Match</c> value: false when it isn't a single version tag or <c>*</c>; <paramref name="version"/> is null for <c>*</c> or an empty value.</summary>
        public static bool TryParseIfMatch(string? header, out int? version)
        {
            version = null;
            var value = header?.Trim();
            if (string.IsNullOrEmpty(value) || value == "*")
            {
                return true;
            }
            if (value.StartsWith("W/", StringComparison.Ordinal))
            {
                value = value[2..];
            }
            if (value.Length >= 3 && value[0] == '"' && value[^1] == '"' && int.TryParse(value[1..^1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                version = n;
                return true;
            }
            return false;
        }

        /// <summary>Joins the expected version from the header and the body: false when they disagree or the header is unusable.</summary>
        public static bool TryResolve(string? ifMatch, int? bodyVersion, out int? expected)
        {
            expected = null;
            if (!TryParseIfMatch(ifMatch, out var header))
            {
                return false;
            }
            if (header is { } h && bodyVersion is { } b && h != b)
            {
                return false;
            }
            expected = header ?? bodyVersion;
            return true;
        }
    }
}
