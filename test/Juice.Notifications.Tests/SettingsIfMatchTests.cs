using Juice.Notifications.Api;

namespace Juice.Notifications.Tests
{
    /// <summary>How a tenant-settings PUT reads the version it expects (M52, #48): <c>If-Match</c> and the body's <c>version</c>.</summary>
    public sealed class SettingsIfMatchTests
    {
        [Theory]
        [InlineData("\"3\"", 3)]
        [InlineData("W/\"0\"", 0)]
        [InlineData(" \"12\" ", 12)]
        public void A_single_version_tag_is_the_expected_version(string header, int expected)
        {
            Assert.True(SettingsVersion.TryParseIfMatch(header, out var version));
            Assert.Equal(expected, version);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("*")]
        public void Nothing_or_a_star_counts_as_omitted(string? header)
        {
            Assert.True(SettingsVersion.TryParseIfMatch(header, out var version));
            Assert.Null(version);
        }

        [Theory]
        [InlineData("3")]
        [InlineData("abc")]
        [InlineData("\"abc\"")]
        [InlineData("\"\"")]
        [InlineData("\"-1\"")]
        [InlineData("\"1\", \"2\"")]
        [InlineData("\"1\",W/\"2\"")]
        [InlineData("\"99999999999\"")]
        [InlineData("*, \"1\"")]
        public void Anything_else_is_refused(string header) => Assert.False(SettingsVersion.TryParseIfMatch(header, out _));

        [Theory]
        [InlineData("\"2\"", null, 2)]
        [InlineData(null, 5, 5)]
        [InlineData("\"2\"", 2, 2)]
        [InlineData("*", 4, 4)]
        [InlineData(null, null, null)]
        public void The_header_and_the_body_agree_or_one_is_missing(string? ifMatch, int? body, int? expected)
        {
            Assert.True(SettingsVersion.TryResolve(ifMatch, body, out var version));
            Assert.Equal(expected, version);
        }

        [Fact]
        public void Different_versions_in_both_are_refused() => Assert.False(SettingsVersion.TryResolve("\"1\"", 2, out _));

        [Fact]
        public void The_etag_is_the_quoted_version() => Assert.Equal("\"7\"", SettingsVersion.ETag(7));
    }
}
