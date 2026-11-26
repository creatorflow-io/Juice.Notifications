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

        /// <summary>
        /// Text comparisons are ordinal (deferred #12): under ICU a culture comparison ignores the soft hyphens (U+00AD) in the subject, so the same
        /// template would render differently on a host with invariant globalization.
        /// </summary>
        [Theory]
        [InlineData("{{ data.subject | string.starts_with 'Disk' }}", "false")]
        [InlineData("{{ data.subject | string.starts_with 'Dis' }}", "true")]
        [InlineData("{{ data.subject | string.ends_with 'full' }}", "false")]
        [InlineData("{{ data.subject | string.ends_with 'l' }}", "true")]
        [InlineData("{{ data.subject | string.index_of 'k' }}", "4")]
        [InlineData("{{ data.subject | string.index_of 'sk' }}", "-1")]
        [InlineData("{{ data.subject | string.index_of 'SK' 0 null 'InvariantCultureIgnoreCase' }}", "-1")]
        [InlineData("{{ data.subject | string.index_of 'FUL' 2 null 'CurrentCultureIgnoreCase' }}", "6")]
        [InlineData("{{ data.subject | string.index_of 'sk' null null '0' }}", "-1")]
        [InlineData("{{ data.subject | string.index_of 'sk' null null ' CurrentCulture' }}", "-1")]
        [InlineData("{{ data.subject | string.index_of 'SK' null null '3' }}", "-1")]
        [InlineData("{{ data.subject | string.index_of 'SK' null null 'currentcultureignorecase' }}", "-1")]
        [InlineData("{{ data.subject | string.equals_ignore_case 'DISK FULL' }}", "false")]
        [InlineData("{{ 'Disk' | string.equals_ignore_case 'DISK' }}", "true")]
        [InlineData("{{ data.missing | string.starts_with 'a' }}", "false")]
        public async Task Text_comparisons_are_ordinal(string template, string expected)
        {
            const char softHyphen = (char)0xAD;
            var data = JsonSerializer.Serialize(new { subject = $"Dis{softHyphen}k ful{softHyphen}l" });
            Assert.Equal(expected, await TemplateRenderer.RenderAsync(template, Model(data), Ct));
        }

        /// <summary>
        /// array.sort compares text ordinally (deferred #12): a soft hyphen counts, and capitals come before small letters. Otherwise it is
        /// Scriban's: by a member or a dotted path, stable, null first; numbers by value whatever their type.
        /// </summary>
        [Theory]
        [InlineData("""{{ ["b", "a", "B", "A"] | array.sort | array.join "," }}""", "A,B,a,b")]
        [InlineData("""{{ data.words | array.sort | array.join "," }}""", "ab,a­a")]
        [InlineData("""{{ [3, 1.5, 2, -1, 12345678901234567890] | array.sort | array.join "," }}""", "-1,1.5,2,3,12345678901234567890")]
        [InlineData("""{{ data.numbers | array.sort | array.join "," }}""", "-2,0.5,1,10")]
        [InlineData("""{{ [true, false] | array.sort | array.join "," }}""", "false,true")]
        // By exact value whatever the types: 2^53 + 1 (an integer), 2^53 + 0.5 (a decimal from the data) and 2^53 (a double) are three values,
        // where comparing some pairs as doubles made them equal and the order depend on the input's.
        [InlineData("""{{ data.close | array.sort | array.join "," }}""", "9007199254740992,9007199254740992.5,9007199254740993")]
        [InlineData("""{{ data.close | array.reverse | array.sort | array.join "," }}""", "9007199254740992,9007199254740992.5,9007199254740993")]
        [InlineData("""{{ [0.1, 0.30000000000000004, 0.3] | array.sort | array.join "," }}""", "0.1,0.3,0.30000000000000004")]
        [InlineData("""{{ ["b", null, "a"] | array.sort | array.size }}""", "3")]
        [InlineData("""{{ (["b", null, "a"] | array.sort)[0] == null }}""", "true")]
        [InlineData("""{{ data.people | array.sort "name" | array.map "id" | array.join "," }}""", "3,1,2,4")]
        [InlineData("""{{ data.people | array.sort "home.city" | array.map "id" | array.join "," }}""", "4,2,1,3")]
        [InlineData("""{{ data.people | array.sort "missing" | array.map "id" | array.join "," }}""", "1,2,3,4")]
        [InlineData("""{{ [] | array.sort | array.size }}""", "0")]
        // One value that doesn't compare, among nulls: nothing to compare, so it sorts (nulls first), as Scriban's did.
        [InlineData("""{{ [{a: 1}] | array.sort | array.size }}""", "1")]
        [InlineData("""{{ ([{a: 1}, null] | array.sort)[1].a }}""", "1")]
        [InlineData("""{{ null | array.sort | array.size }}""", "0")]
        [InlineData("""{{ 5 | array.sort | array.join "," }}""", "5")]
        [InlineData("""{{ "cab" | array.sort | array.join "" }}""", "abc")]
        public async Task Sorting_is_ordinal(string template, string expected)
        {
            var data = """
                {
                  "words": [ "a­a", "ab" ],
                  "numbers": [ 10, 0.5, -2, 1 ],
                  "close": [ 9007199254740993, 9007199254740992.5, 9007199254740992.0E0 ],
                  "people": [
                    { "id": 1, "name": "bo", "home": { "city": "Oslo" } },
                    { "id": 2, "name": "bo", "home": { "city": "Hue" } },
                    { "id": 3, "name": "Zed", "home": { "city": "Paris" } },
                    { "id": 4, "name": "cy", "home": { "city": "Aden" } }
                  ]
                }
                """;
            Assert.Equal(expected, await TemplateRenderer.RenderAsync(template, Model(data), Ct));
        }

        [Theory]
        [InlineData("""{{ ["a", 1] | array.sort }}""", "can't compare text with a number")]
        [InlineData("""{{ [true, null, "a"] | array.sort }}""", "can't compare a boolean with text")]
        [InlineData("""{{ [{a: 1}, {a: 2}] | array.sort }}""", "can't compare an object values")]
        [InlineData("""{{ data.people | array.sort "home" }}""", "can't compare an object values")]
        [InlineData("""{{ data.many | array.sort }}""", "iteration limit")]
        public async Task Sorting_what_doesnt_compare_or_too_much_fails_the_render_and_says_why(string template, string why)
        {
            var data = $$"""{ "people": [ { "home": { "city": "Oslo" } }, { "home": { "city": "Hue" } } ], "many": [ {{string.Join(", ", Enumerable.Range(0, 1001))}} ] }""";
            var failed = await Assert.ThrowsAsync<RenderException>(() => TemplateRenderer.RenderAsync(template, Model(data), Ct));
            Assert.Contains(why, failed.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("nope")]
        [InlineData("9")]
        public async Task A_comparison_that_isnt_one_fails_the_render(string comparison)
            => await Assert.ThrowsAsync<RenderException>(() => TemplateRenderer.RenderAsync($"{{{{ 'abc' | string.index_of 'b' null null '{comparison}' }}}}", Model(), Ct));

        /// <summary>The declaration check refuses what the sandbox withholds wherever it appears, including branches data may never reach.</summary>
        [Theory]
        [InlineData("{{ include 'other' }}")]
        [InlineData("{{ if data.flag }}{{ include 'other' }}{{ end }}")]
        [InlineData("{{ object.eval_template '{{ 1 }}' }}")]
        [InlineData("{{ date.now }}")]
        [InlineData("{{ regex.match data.subject '.' }}")]
        [InlineData("{{ import data }}")]
        [InlineData("{{ if }}")]
        public void The_check_refuses_what_the_sandbox_withholds(string template) => Assert.NotNull(TemplateRenderer.Check(template));

        /// <summary>It doesn't render, so a template that would fail on empty data (arithmetic on a missing field) is fine, and so are its own names.</summary>
        [Theory]
        [InlineData("{{ data.count + 1 }}")]
        [InlineData("{{ x = data.items | array.size }}{{ x }}")]
        [InlineData("{{ for i in data.items }}{{ for.index }}{{ i }}{{ end }}")]
        [InlineData("{{ func twice(v); ret v * 2; end; twice 2 }}")]
        [InlineData("{\"text\": \"{{ data.subject | json.escape }}\", \"data\": {{ data | json.encode }}}")]
        // A withheld name in a position that isn't the global scope is the template's own: a member, a key, a parameter, a loop variable, a with block.
        [InlineData("{{ data.date }} {{ labels.regex }} {{ data.object.include }}")]
        [InlineData("{{ x = {date: 1} }}{{ x.date }}")]
        [InlineData("{{ func f(date); ret date; end; f 1 }}")]
        [InlineData("{{ for object in data.items }}{{ object.a }}{{ end }}")]
        [InlineData("{{ with data }}{{ date }}{{ end }}")]
        public void The_check_accepts_what_only_data_could_break(string template) => Assert.Null(TemplateRenderer.Check(template));

        /// <summary>Limits stop a render: a loop that doesn't end and a recursion that doesn't end.</summary>
        [Theory]
        [InlineData("{{ while true }}x{{ end }}")]
        [InlineData("{{ func f; ret f; end; f }}")]
        public async Task A_render_that_wouldnt_end_is_refused(string template)
            => Assert.False((await Assert.ThrowsAsync<RenderException>(() => TemplateRenderer.RenderAsync(template, Model(), Ct))).Retryable);

        /// <summary>A render stopped because the worker is stopping is a cancellation, never a template failure recorded as render_failed.</summary>
        [Fact]
        public async Task Stopping_cancels_the_render_rather_than_failing_the_template()
        {
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TemplateRenderer.RenderAsync("{{ for i in 1..10 }}{{ i }}{{ end }}", Model(), stop.Token));
        }

        /// <summary>A webhook template builds JSON safely: a data value can't close a string and add fields.</summary>
        [Fact]
        public async Task Json_functions_keep_data_inside_the_json()
        {
            var model = Model("""{ "text": "a\",\"admin\":true,\"x\":\"", "n": 2, "list": [ 1, "b" ] }""");
            var output = await TemplateRenderer.RenderAsync("{\"msg\":\"{{ data.text | json.escape }}\",\"data\":{{ data | json.encode }}}", model, Ct);
            using var json = JsonDocument.Parse(output);
            Assert.Equal("a\",\"admin\":true,\"x\":\"", json.RootElement.GetProperty("msg").GetString());
            Assert.False(json.RootElement.TryGetProperty("admin", out _));
            Assert.Equal(2, json.RootElement.GetProperty("data").GetProperty("n").GetInt32());
            Assert.Equal("b", json.RootElement.GetProperty("data").GetProperty("list")[1].GetString());
        }

        /// <summary>
        /// json.encode is held to the sandbox's limits inside its one call: a value that contains itself, or one built from shared parts that
        /// would be enormous written out, fails the render instead of overflowing the stack or exhausting memory.
        /// </summary>
        [Theory]
        [InlineData("{{ a = {} }}{{ a.self = a }}{{ a | json.encode }}")]
        [InlineData("{{ a = [1,2,3,4,5,6,7,8] }}{{ for i in 1..26 }}{{ a = [a, a] }}{{ end }}{{ a | json.encode | string.size }}")]
        public async Task Json_encode_stays_within_the_limits(string template)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var failure = await Assert.ThrowsAsync<RenderException>(() => TemplateRenderer.RenderAsync(template, Model(), Ct));
            Assert.Contains("json.encode", failure.Message);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        }

        /// <summary>Values come out as the template prints them and as the data had them: true not True, a 20-digit id exact.</summary>
        [Fact]
        public async Task Json_functions_keep_values_as_they_were()
        {
            var model = Model("""{ "ok": true, "id": 12345678901234567890 }""");
            Assert.Equal("true", await TemplateRenderer.RenderAsync("{{ data.ok | json.escape }}", model, Ct));
            Assert.Equal("""{"ok":true,"id":12345678901234567890}""", await TemplateRenderer.RenderAsync("{{ data | json.encode }}", model, Ct));
        }

        /// <summary>Nothing reads the host clock: date isn't in the sandbox (the check refuses it; a render shows nothing).</summary>
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
            // A subject can't smuggle in a header line, Unicode line breaks included.
            Assert.Equal("a b", JsonSerializer.Deserialize<EmailContent>(ChannelContent.Email("Subject: a\r b\n\nbody", "F"))!.Subject);
            Assert.Equal("a b c", JsonSerializer.Deserialize<EmailContent>(ChannelContent.Email("Subject: a\u2028b\u0085c\n\nbody", "F"))!.Subject);
        }

        /// <summary>
        /// A member or index chain Scriban parses at any length but renders recursively (about 500 links overflow the stack and end the process):
        /// refused at declaration without overflowing the check, and refused before rendering a stored one.
        /// </summary>
        [Theory]
        [InlineData(".a", 300)]
        [InlineData(".a", 30_000)]
        [InlineData("[0]", 1_000)]
        public async Task A_chain_too_deep_is_refused_before_it_overflows_the_stack(string link, int count)
        {
            var template = "{{ data" + string.Concat(Enumerable.Repeat(link, count)) + " }}";
            if (template.Length <= TemplateRenderer.MaxTemplateChars)
            {
                Assert.Contains("nests too deeply", TemplateRenderer.Check(template));
            }
            await Assert.ThrowsAsync<RenderException>(() => TemplateRenderer.RenderAsync(template, Model(), Ct));
        }

        /// <summary>
        /// A template's own recursive function multiplies its body's depth by the call depth: shallow enough for the check, but past a pool
        /// thread's stack. The render has a stack of its own, so it renders instead of ending the process.
        /// </summary>
        [Fact]
        public async Task A_recursive_function_with_a_deep_body_renders_on_its_own_stack()
        {
            var pipes = string.Concat(Enumerable.Repeat(" | string.upcase", 60));
            var template = "{{ func f(n) }}{{ if n > 0 }}{{ ret (f (n - 1))" + pipes + " }}{{ end }}{{ ret 'a' }}{{ end }}{{ f 15 }}";
            Assert.Null(TemplateRenderer.Check(template));
            Assert.Equal("A", await TemplateRenderer.RenderAsync(template, Model(), Ct));
        }

        /// <summary>
        /// A template that fails on its own (the recursion limit) is the template's failure even when unwinding it takes past the time budget: a
        /// retry could never succeed.
        /// </summary>
        [Fact]
        public async Task A_deep_recursion_failure_is_not_taken_for_a_timeout()
        {
            var body = string.Concat(Enumerable.Repeat(" | string.replace 'q' 'z'", 236));
            var template = "{{ func f(n) }}{{ x = (f (n - 1))" + body + " }}{{ ret x }}{{ end }}{{ f 30 }}";
            Assert.Null(TemplateRenderer.Check(template));
            var failure = await Assert.ThrowsAsync<RenderException>(() => TemplateRenderer.RenderAsync(template, Model(), Ct));
            Assert.False(failure.Retryable, failure.Message);
        }

        /// <summary>Long ladders real templates use (a code-to-label table) are within the depth limit.</summary>
        [Fact]
        public async Task A_long_else_if_ladder_is_accepted()
        {
            var template = "{{ if data.code == 0 }}zero" + string.Concat(Enumerable.Range(1, 120).Select(i => $"{{{{ else if data.code == {i} }}}}c{i}")) + "{{ end }}";
            Assert.Null(TemplateRenderer.Check(template));
            Assert.Equal("c120", await TemplateRenderer.RenderAsync(template, Model("""{ "code": 120 }"""), Ct));
        }

        /// <summary>The host's culture doesn't reach the output: the render runs in the invariant culture, and array.sort compares ordinally.</summary>
        [Fact]
        public async Task The_hosts_culture_does_not_change_the_output()
        {
            var original = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
                Assert.Equal("z,ä", await TemplateRenderer.RenderAsync("""{{ ["z","ä"] | array.sort | array.join "," }}""", Model(), Ct));
                Assert.Equal("1.5", await TemplateRenderer.RenderAsync("{{ 1.5 }}", Model(), Ct));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public async Task A_chain_of_ordinary_depth_renders()
        {
            var template = "{{ data" + string.Concat(Enumerable.Repeat(".a", 20)) + " }}";
            Assert.Null(TemplateRenderer.Check(template));
            Assert.Equal("", await TemplateRenderer.RenderAsync(template, Model(), Ct));
        }

        /// <summary>Running out of time is retryable, also when the time runs out inside a built-in (Scriban wraps what it throws).</summary>
        [Theory]
        [InlineData("{{ s = 'x' | string.pad_left 30000 }}{{ a = [s,s,s,s,s,s,s,s] }}{{ for i in 1..1000 }}{{ a | json.encode | string.size }}{{ end }}")]
        public async Task A_slow_template_runs_out_of_time_as_a_retryable_failure(string template)
        {
            var failure = await Assert.ThrowsAsync<RenderException>(() => TemplateRenderer.RenderAsync(template, Model(), Ct));
            Assert.True(failure.Retryable, failure.Message);
        }

        /// <summary>Nothing that differs between renders: math.random and math.uuid are refused at declaration and absent at render.</summary>
        [Theory]
        [InlineData("{{ math.uuid }}")]
        [InlineData("{{ math.random 1 100 }}")]
        public async Task Random_values_are_not_in_the_sandbox(string template)
        {
            Assert.Contains("differs on every render", TemplateRenderer.Check(template));
            var first = await RenderOrFailure(template);
            Assert.Equal(first, await RenderOrFailure(template));
        }

        private static async Task<string> RenderOrFailure(string template)
        {
            try
            {
                return await TemplateRenderer.RenderAsync(template, Model(), Ct);
            }
            catch (RenderException ex)
            {
                return ex.Message;
            }
        }

        [Fact]
        public async Task A_tiny_number_is_not_rounded_to_zero()
        {
            var model = Model("""{ "tiny": 1e-30, "exact": 0.10, "big": 12345678901234567890 }""");
            Assert.Equal("""{"tiny":1E-30,"exact":0.10,"big":12345678901234567890}""", await TemplateRenderer.RenderAsync("{{ data | json.encode }}", model, Ct));
        }

        [Fact]
        public void A_name_the_template_captures_into_is_its_own()
            => Assert.Null(TemplateRenderer.Check("{{ capture date }}x{{ end }}{{ date }}"));

        /// <summary>A long subject is cut at 250, never through the middle of a character such as an emoji.</summary>
        [Fact]
        public void A_long_subject_is_cut_on_a_character_boundary()
        {
            var subject = new string('a', 249) + "😀" + "tail";
            var content = JsonSerializer.Deserialize<EmailContent>(ChannelContent.Email("Subject: " + subject + "\n\nbody", "F"))!;
            Assert.Equal(new string('a', 249), content.Subject);
        }

        /// <summary>An inbox item (phase-2-plan I17): its title from a first line, else the type's name; its link from links.self, if http(s).</summary>
        [Fact]
        public void An_inbox_item_takes_its_title_from_the_first_line_and_its_link_from_self()
        {
            var links = new Dictionary<string, string> { ["self"] = "https://app.example.com/alerts/1" };
            var item = JsonSerializer.Deserialize<InboxContent>(ChannelContent.Inbox("Title: Disk full\n\nHost db-1\n", "app.disk", "Fallback", links,
                """[{"id":"ack","label":"Acknowledge"}]"""))!;
            Assert.Equal(("app.disk", "Disk full", "Host db-1", "https://app.example.com/alerts/1"), (item.Type, item.Title, item.Body, item.Url));
            Assert.Equal("ack", item.Actions!.Value[0].GetProperty("id").GetString());

            // No template: the type's name, no body. A link that isn't http(s) isn't kept, nor is a title split over lines.
            var bare = JsonSerializer.Deserialize<InboxContent>(ChannelContent.Inbox(null, "app.disk", "Fallback",
                new Dictionary<string, string> { ["self"] = "javascript:alert(1)" }, null))!;
            Assert.Equal(("Fallback", "", null), (bare.Title, bare.Body, bare.Url));
            Assert.Null(bare.Actions);
            Assert.Equal("a b", JsonSerializer.Deserialize<InboxContent>(ChannelContent.Inbox("Title: a" + (char)0x2028 + "b" + (char)10 + "body", "t", "F", null, null))!.Title);
            Assert.Equal("F", JsonSerializer.Deserialize<InboxContent>(ChannelContent.Inbox("Title:   \nbody", "t", "F", null, null))!.Title);
        }

        /// <summary>A type's inbox actions (design §7.3): up to five, ids distinct slugs, labels short; stored canonical, and hashed only when declared.</summary>
        [Fact]
        public void A_type_declares_up_to_five_actions()
        {
            static (Types.CheckedType? Type, IReadOnlyList<FieldError> Errors) Check(string? actions)
                => Types.TypeRules.Check("app", new Types.TypeDeclaration("app.thing", "Thing", "ops", null, null, null, null,
                    JsonElement.Parse("""{ "type": "object" }"""), null, null, null, null, actions is null ? null : JsonElement.Parse(actions)),
                    new ChannelRegistry([], BuiltInChannelFallback.Instance));

            var (type, errors) = Check("""[ { "label": "Approve", "id": "approve" }, { "id": "reject", "label": "Reject" } ]""");
            Assert.Empty(errors);
            Assert.Equal("""[{"id":"approve","label":"Approve"},{"id":"reject","label":"Reject"}]""", type!.Actions);
            Assert.NotEqual(Check(null).Type!.Hash(), type.Hash());
            Assert.Null(Check("[]").Type!.Actions);
            Assert.Equal(Check(null).Type!.Hash(), Check("[]").Type!.Hash());

            Assert.Contains(Check("""[ { "id": "a", "label": "A" }, { "id": "a", "label": "B" } ]""").Errors, e => e.Pointer == "/actions/1/id");
            Assert.Contains(Check("""[ { "id": "Approve", "label": "A" } ]""").Errors, e => e.Pointer == "/actions/0/id");
            Assert.Contains(Check("""[ { "id": "a", "label": " A" } ]""").Errors, e => e.Pointer == "/actions/0/label");
            Assert.Contains(Check("""[ { "id": "a", "label": "A", "url": "x" } ]""").Errors, e => e.Pointer == "/actions/0");
            Assert.Contains(Check("""{ "id": "a" }""").Errors, e => e.Pointer == "/actions");
            Assert.Contains(Check("""[ {"id":"a","label":"A"}, {"id":"b","label":"B"}, {"id":"c","label":"C"}, {"id":"d","label":"D"}, {"id":"e","label":"E"}, {"id":"f","label":"F"} ]""").Errors,
                e => e.Pointer == "/actions");
        }

        /// <summary>The delivery a template renders for, and the escalation step that sends it (phase-5 M46-0): the legacy webhook payload needs both.</summary>
        [Fact]
        public async Task Templates_see_the_delivery_id_and_the_escalation_step()
        {
            var id = Guid.CreateVersion7();
            var model = Model() with { Delivery = new RenderDelivery(id), Escalation = new RenderEscalation(2, "paging") };
            Assert.Equal($"{id} 2 paging", await TemplateRenderer.RenderAsync("{{ delivery.id }} {{ escalation.step }} {{ escalation.policy }}", model, Ct));
            Assert.Equal($"\"{id}\" 2", await TemplateRenderer.RenderAsync("{{ delivery.id | json.encode }} {{ escalation.step | json.encode }}", model, Ct));
            // Not an escalation: escalation is empty, so a template can tell.
            Assert.Equal("plain", await TemplateRenderer.RenderAsync("{{ if escalation }}step{{ else }}plain{{ end }}", Model() with { Delivery = new RenderDelivery(id) }, Ct));
            Assert.Null(TemplateRenderer.Check("{{ delivery.id }}{{ escalation.step }}{{ escalation.policy }}"));
        }

        /// <summary>Whether a template reads <c>delivery</c>, from its syntax (phase-5 M46-0): only then is it rendered for each delivery.</summary>
        [Theory]
        [InlineData("{{ delivery.id }}", true)]
        [InlineData("{{ if delivery }}x{{ end }}", true)]
        [InlineData("{{ for x in [1] }}{{ delivery.id | json.encode }}{{ end }}", true)]
        [InlineData("{{ data.delivery }} {{ labels.delivery }}", false)]
        [InlineData("{{ escalation.step }} {{ data.host }}", false)]
        [InlineData("plain text", false)]
        [InlineData("{{ if }", true)]
        public void A_template_reads_the_delivery_only_when_it_names_it(string body, bool uses)
            => Assert.Equal(uses, TemplateRenderer.UsesDelivery(body));
    }
}
