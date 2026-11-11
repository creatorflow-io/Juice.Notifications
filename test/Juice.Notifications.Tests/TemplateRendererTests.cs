using System.Text.Json;
using Juice.Notifications.Rendering;

namespace Juice.Notifications.Tests
{
    /// <summary>The Scriban sandbox (design §8.2) and each channel's content (§8.1).</summary>
    public sealed class TemplateRendererTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static RenderModel Model(string data = """{ "subject": "Disk full", "host": "db-1", "items": [ 1, 2, 3 ], "nested": { "a": "b" } }""")
            => new("observability.alert.firing", 3, "Alert firing", NotificationPriority.High, JsonElement.Parse(data),
                new Dictionary<string, string> { ["env"] = "prod" }, new Dictionary<string, string> { ["self"] = "https://example.com/a" });

        [Theory]
        [InlineData("{{ data.subject }} on {{ data.host }}", "Disk full on db-1")]
        [InlineData("{{ data.nested.a }}", "b")]
        [InlineData("{{ for i in data.items }}{{ i }}{{ end }}", "123")]
        [InlineData("{{ labels.env }} {{ links.self }}", "prod https://example.com/a")]
        [InlineData("{{ type }} v{{ type_version }} {{ priority }}", "observability.alert.firing v3 high")]
        [InlineData("{{ data.subject | string.upcase }}", "DISK FULL")]
        [InlineData("[{{ data.missing }}][{{ data.missing.deeper }}]", "[][]")]
        [InlineData("{{ data.items | array.size }}", "3")]
        public async Task Templates_see_the_model_and_the_allowed_builtins(string template, string expected)
            => Assert.Equal(expected, await TemplateRenderer.RenderAsync(template, Model(), Ct));

        [Theory]
        [InlineData("{{ include 'other' }}")]
        [InlineData("{{ object.eval_template '{{ 1 }}' }}")]
        [InlineData("{{ while true }}x{{ end }}")]
        [InlineData("{{ func f; ret f; end; f }}")]
        public async Task The_sandbox_refuses_what_it_does_not_allow(string template)
        {
            var problem = await TemplateRenderer.CheckAsync(template, Ct);
            Assert.NotNull(problem);
        }

        /// <summary>Nothing reads the host clock: date isn't in the sandbox, so it renders empty.</summary>
        [Fact]
        public async Task There_is_no_clock_in_the_sandbox()
            => Assert.Equal("[]", await TemplateRenderer.RenderAsync("[{{ date.now }}]", Model(), Ct));

        [Fact]
        public async Task Output_over_the_limit_is_refused()
            => await Assert.ThrowsAsync<RenderException>(() => TemplateRenderer.RenderAsync(
                "{{ for i in 1..1000 }}" + new string('x', 400) + "{{ end }}", Model(), Ct));

        [Fact]
        public void Email_takes_its_subject_from_the_first_line_and_encodes_the_html()
        {
            var content = JsonSerializer.Deserialize<EmailContent>(ChannelContent.Email("Subject: Disk full\n\nHost <b>db-1</b> & co\nline 2\n", "Fallback"))!;
            Assert.Equal("Disk full", content.Subject);
            Assert.Equal("Host <b>db-1</b> & co\nline 2", content.Text);
            Assert.Contains("Host &lt;b&gt;db-1&lt;/b&gt; &amp; co", content.Html);
            Assert.DoesNotContain("<b>", content.Html);

            Assert.Equal("Fallback", JsonSerializer.Deserialize<EmailContent>(ChannelContent.Email("just a body", "Fallback"))!.Subject);
            // A subject can't smuggle in a header line.
            Assert.Equal("a b", JsonSerializer.Deserialize<EmailContent>(ChannelContent.Email("Subject: a\r b\n\nbody", "F"))!.Subject);
        }
    }
}
