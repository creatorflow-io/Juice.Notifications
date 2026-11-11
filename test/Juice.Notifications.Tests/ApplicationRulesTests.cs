using Juice.Notifications.Applications;

namespace Juice.Notifications.Tests
{
    public sealed class ApplicationRulesTests
    {
        [Theory]
        [InlineData("observability", true)]
        [InlineData("ab", true)]
        [InlineData("my-app-2", true)]
        [InlineData("a", false)]
        [InlineData("Observability", false)]
        [InlineData("2fa", false)]
        [InlineData("-app", false)]
        [InlineData("has.dot", false)]
        [InlineData("has space", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Keys_are_type_namespaces(string? key, bool valid) => Assert.Equal(valid, ApplicationRules.CheckKey(key) is null);

        [Fact]
        public void Keys_are_at_most_63_characters()
        {
            Assert.Null(ApplicationRules.CheckKey("a" + new string('b', 62)));
            Assert.NotNull(ApplicationRules.CheckKey("a" + new string('b', 63)));
        }

        [Theory]
        [InlineData("sample-producer", true)]
        [InlineData("svc:observability@juice", true)]
        [InlineData("has space", false)]
        [InlineData("", false)]
        public void Client_ids_are_printable_without_spaces(string clientId, bool valid) => Assert.Equal(valid, ApplicationRules.CheckClientId(clientId) is null);

        [Theory]
        [InlineData("Observability", true)]
        [InlineData("   ", false)]
        [InlineData("", false)]
        public void Names_are_not_blank(string name, bool valid) => Assert.Equal(valid, ApplicationRules.CheckName(name) is null);
    }
}
