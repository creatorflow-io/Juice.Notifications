using System.Text.Json;
using Juice.Notifications.People;
using Juice.Notifications.Planning;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// Quiet hours (design §6.4, phase-2-plan I21): periods by local wall-clock time, in the person's time zone, across midnight and across both
    /// DST changes. Europe/Berlin changes on 29 March 2026 (02:00 → 03:00) and 25 October 2026 (03:00 → 02:00).
    /// </summary>
    public sealed class QuietHoursTests
    {
        private static QuietHours Hours(string start, string end, string? zone = null, params string[] days)
        {
            var (hours, errors) = QuietHours.From(new QuietHoursSpec(days.Length == 0 ? null : days, start, end, zone));
            Assert.Empty(errors);
            return hours!;
        }

        private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso + "Z", System.Globalization.CultureInfo.InvariantCulture);

        [Theory]
        // Within one day: 09:00–17:00 UTC.
        [InlineData("09:00", "17:00", "2026-11-16T08:59:00", null)]
        [InlineData("09:00", "17:00", "2026-11-16T09:00:00", "2026-11-16T17:00:00")]
        [InlineData("09:00", "17:00", "2026-11-16T16:59:00", "2026-11-16T17:00:00")]
        [InlineData("09:00", "17:00", "2026-11-16T17:00:00", null)]
        // Past midnight: 22:00–07:00 UTC.
        [InlineData("22:00", "07:00", "2026-11-16T21:59:00", null)]
        [InlineData("22:00", "07:00", "2026-11-16T22:00:00", "2026-11-17T07:00:00")]
        [InlineData("22:00", "07:00", "2026-11-17T03:00:00", "2026-11-17T07:00:00")]
        [InlineData("22:00", "07:00", "2026-11-17T07:00:00", null)]
        public void A_period_runs_from_its_start_to_its_end(string start, string end, string now, string? until)
            => Assert.Equal(until is null ? null : Utc(until), Hours(start, end).EndIfQuiet(Utc(now), null));

        [Fact]
        public void A_period_belongs_to_the_day_it_starts_on()
        {
            // Fridays 22:00–07:00: Saturday morning is quiet, Saturday night isn't; 16 November 2026 is a Monday.
            var fridays = Hours("22:00", "07:00", null, "fri");
            Assert.Equal(Utc("2026-11-21T07:00:00"), fridays.EndIfQuiet(Utc("2026-11-21T01:00:00"), null));
            Assert.Null(fridays.EndIfQuiet(Utc("2026-11-21T23:00:00"), null));
            Assert.Null(fridays.EndIfQuiet(Utc("2026-11-19T23:00:00"), null));
            var weekdays = Hours("09:00", "17:00", null, "mon", "tue", "wed", "thu", "fri");
            Assert.NotNull(weekdays.EndIfQuiet(Utc("2026-11-16T10:00:00"), null));
            Assert.Null(weekdays.EndIfQuiet(Utc("2026-11-22T10:00:00"), null));
        }

        [Fact]
        public void The_time_zone_is_the_quiet_hours_own_then_the_persons_then_utc()
        {
            // 22:00–07:00 in Ho Chi Minh City (UTC+7): 16:00 UTC is 23:00 there.
            var now = Utc("2026-11-16T16:00:00");
            Assert.Equal(Utc("2026-11-17T00:00:00"), Hours("22:00", "07:00", "Asia/Ho_Chi_Minh").EndIfQuiet(now, "Europe/Berlin"));
            Assert.Equal(Utc("2026-11-17T00:00:00"), Hours("22:00", "07:00").EndIfQuiet(now, "Asia/Ho_Chi_Minh"));
            Assert.Null(Hours("22:00", "07:00").EndIfQuiet(now, null));
            // A zone this host doesn't know (a stored one since retired) reads as UTC rather than failing the plan.
            Assert.Null(Hours("22:00", "07:00").EndIfQuiet(now, "Mars/Olympus_Mons"));
        }

        [Fact]
        public void Quiet_hours_keep_their_wall_clock_hours_when_the_clocks_go_forward()
        {
            var nights = Hours("22:00", "07:00", "Europe/Berlin");
            // 23:00 CET on 28 March (22:00 UTC) until 07:00 CEST on 29 March (05:00 UTC): seven hours, not eight.
            Assert.Equal(Utc("2026-03-29T05:00:00"), nights.EndIfQuiet(Utc("2026-03-28T22:00:00"), null));
            // After the change, 06:30 CEST (04:30 UTC) is still quiet, until the same 07:00.
            Assert.Equal(Utc("2026-03-29T05:00:00"), nights.EndIfQuiet(Utc("2026-03-29T04:30:00"), null));
            Assert.Null(nights.EndIfQuiet(Utc("2026-03-29T05:00:00"), null));
        }

        [Fact]
        public void An_end_the_clocks_skip_is_when_they_jump()
        {
            // 02:30 doesn't exist on 29 March in Berlin: the period ends at 03:00 CEST (01:00 UTC), when the clocks jump past it.
            var nights = Hours("22:00", "02:30", "Europe/Berlin");
            Assert.Equal(Utc("2026-03-29T01:00:00"), nights.EndIfQuiet(Utc("2026-03-28T22:00:00"), null));
            Assert.Null(nights.EndIfQuiet(Utc("2026-03-29T01:00:00"), null));
        }

        [Fact]
        public void Quiet_hours_keep_their_wall_clock_hours_when_the_clocks_go_back()
        {
            var nights = Hours("22:00", "07:00", "Europe/Berlin");
            // 23:00 CEST on 24 October (21:00 UTC) until 07:00 CET on 25 October (06:00 UTC): nine hours, not eight.
            Assert.Equal(Utc("2026-10-25T06:00:00"), nights.EndIfQuiet(Utc("2026-10-24T21:00:00"), null));
            Assert.Null(nights.EndIfQuiet(Utc("2026-10-25T06:00:00"), null));
        }

        [Fact]
        public void An_end_the_clocks_show_twice_is_the_first_time_it_comes()
        {
            // 02:30 comes twice on 25 October in Berlin: at 00:30 UTC (CEST) and 01:30 UTC (CET). The period ends the first time, and doesn't come
            // back when the clock shows 02:00–02:29 again.
            var nights = Hours("22:00", "02:30", "Europe/Berlin");
            Assert.Equal(Utc("2026-10-25T00:30:00"), nights.EndIfQuiet(Utc("2026-10-24T21:00:00"), null));
            Assert.Equal(Utc("2026-10-25T00:30:00"), nights.EndIfQuiet(Utc("2026-10-25T00:10:00"), null));
            Assert.Null(nights.EndIfQuiet(Utc("2026-10-25T00:45:00"), null));
            Assert.Null(nights.EndIfQuiet(Utc("2026-10-25T01:00:00"), null));
            Assert.Null(nights.EndIfQuiet(Utc("2026-10-25T01:10:00"), null));
            // New York's 01:30 on 1 November: 05:30 UTC (EDT), not 06:30 (EST).
            var early = Hours("22:00", "01:30", "America/New_York");
            Assert.Equal(Utc("2026-11-01T05:30:00"), early.EndIfQuiet(Utc("2026-11-01T05:00:00"), null));
            Assert.Null(early.EndIfQuiet(Utc("2026-11-01T06:10:00"), null));
        }

        [Fact]
        public void A_start_the_clocks_show_twice_is_the_first_time_it_comes()
        {
            // 02:15–02:45 on 25 October in Berlin: from 02:15 CEST (00:15 UTC) to 02:45 CEST (00:45 UTC), once.
            var once = Hours("02:15", "02:45", "Europe/Berlin");
            Assert.Equal(Utc("2026-10-25T00:45:00"), once.EndIfQuiet(Utc("2026-10-25T00:20:00"), null));
            Assert.Null(once.EndIfQuiet(Utc("2026-10-25T01:20:00"), null));
        }

        /// <summary>What the person sees of stored quiet hours is exactly what planning applies; a value changed by hand that doesn't hold is neither.</summary>
        [Theory]
        [InlineData("""{"days":["mon"],"start":"22:00","end":"07:00","timeZone":"Europe/Berlin"}""", true)]
        [InlineData("""{"days":null,"start":"22:00","end":"07:00","timeZone":"Mars/Olympus_Mons"}""", true)]
        [InlineData("""{"days":["Mon"],"start":"22:00","end":"07:00"}""", false)]
        [InlineData("""{"days":"mon","start":"22:00","end":"07:00"}""", false)]
        [InlineData("""{"start":"24:00","end":"07:00"}""", false)]
        [InlineData("""{"start":"22:00","end":"22:00"}""", false)]
        [InlineData("""{"start":"22:00"}""", false)]
        [InlineData("""not json""", false)]
        public void Stored_quiet_hours_are_shown_exactly_when_they_apply(string stored, bool holds)
        {
            Assert.Equal(holds, QuietHours.TryRead(stored) is not null);
            Assert.Equal(holds, QuietHours.Parse(stored) is not null);
        }

        private static Data.Preference P(string category, string channel, string mode)
            => new() { Category = category, Channel = channel, Mode = mode };

        [Fact]
        public void The_most_specific_preference_wins()
        {
            Data.Preference[] prefs = [P("*", "*", "off"), P("billing", "email", "on"), P("*", "webhook", "on"), P("security", "*", "on"), P("security", "email", "off")];
            Assert.Equal("on", PreferencePolicy.Mode(prefs, "billing", "email"));
            Assert.Equal("off", PreferencePolicy.Mode(prefs, "billing", "inbox"));
            Assert.Equal("on", PreferencePolicy.Mode(prefs, "billing", "webhook"));
            Assert.Equal("on", PreferencePolicy.Mode(prefs, "security", "inbox"));
            Assert.Equal("off", PreferencePolicy.Mode(prefs, "security", "email"));
            Assert.Equal("on", PreferencePolicy.Mode([], "billing", "email"));
            // What a mandatory type keeps: only choices naming the channel.
            Assert.Equal("on", PreferencePolicy.Mode(prefs, "other", "email", channelOnly: true));
            Assert.Equal("off", PreferencePolicy.Mode(prefs, "security", "email", channelOnly: true));
        }

        [Fact]
        public void A_category_is_opted_out_only_when_every_channel_is_off()
        {
            Assert.True(PreferencePolicy.OptedOut([P("billing", "*", "off")], "billing"));
            Assert.True(PreferencePolicy.OptedOut([P("*", "*", "off")], "billing"));
            Assert.True(PreferencePolicy.OptedOut([P("*", "*", "off"), P("*", "email", "off")], "billing"));
            // One channel turned back on keeps the category on it.
            Assert.False(PreferencePolicy.OptedOut([P("*", "*", "off"), P("billing", "email", "on")], "billing"));
            Assert.False(PreferencePolicy.OptedOut([P("*", "*", "off"), P("*", "email", "digest")], "billing"));
            Assert.True(PreferencePolicy.OptedOut([P("*", "*", "off"), P("security", "email", "on")], "billing"));
            Assert.False(PreferencePolicy.OptedOut([P("*", "*", "off"), P("billing", "*", "on")], "billing"));
            Assert.False(PreferencePolicy.OptedOut([P("billing", "email", "off")], "billing"));
        }

        [Fact]
        public void New_york_changes_on_other_days()
        {
            // 8 March 2026 (02:00 → 03:00 EDT) and 1 November 2026 (02:00 → 01:00 EST); 21:00–06:00.
            var nights = Hours("21:00", "06:00", "America/New_York");
            Assert.Equal(Utc("2026-03-08T10:00:00"), nights.EndIfQuiet(Utc("2026-03-08T03:00:00"), null)); // 22:00 EST until 06:00 EDT
            Assert.Equal(Utc("2026-11-01T11:00:00"), nights.EndIfQuiet(Utc("2026-11-01T02:00:00"), null)); // 22:00 EDT until 06:00 EST
        }

        [Theory]
        [InlineData(null, "07:00", null, "/quietHours/start")]
        [InlineData("7:00", "07:00", null, "/quietHours/start")]
        [InlineData("24:00", "07:00", null, "/quietHours/start")]
        [InlineData("22:00", "22:00", null, "/quietHours/end")]
        [InlineData("22:00", "07:60", null, "/quietHours/end")]
        [InlineData("22:00", "07:00", "Not/AZone", "/quietHours/timeZone")]
        public void Quiet_hours_are_checked(string? start, string? end, string? zone, string pointer)
            => Assert.Contains(QuietHours.From(new QuietHoursSpec(null, start, end, zone)).Errors, e => e.Pointer == pointer);

        [Theory]
        [InlineData("")]
        [InlineData("monday")]
        [InlineData("mon,mon")]
        public void Days_are_distinct_day_names(string days)
            => Assert.Contains(QuietHours.From(new QuietHoursSpec(days.Split(',', StringSplitOptions.RemoveEmptyEntries), "22:00", "07:00", null)).Errors, e => e.Pointer == "/quietHours/days");

        [Fact]
        public void Quiet_hours_are_stored_canonically()
        {
            Assert.Equal("""{"days":["mon","fri"],"start":"22:00","end":"07:00","timeZone":"Europe/Berlin"}""",
                QuietHours.Store(new QuietHoursSpec(["fri", "mon"], "22:00", "07:00", "Europe/Berlin")));
            Assert.Equal("""{"days":null,"start":"22:00","end":"07:00","timeZone":null}""",
                QuietHours.Store(new QuietHoursSpec(["sun", "mon", "tue", "wed", "thu", "fri", "sat"], "22:00", "07:00", null)));
            Assert.Null(QuietHours.Parse(null));
        }

        [Theory]
        [InlineData("""{ "type": "app.alert" }""", new[] { "app.alert" })]
        [InlineData("""{ "type": ["app.alert", "app.page"] }""", new[] { "app.alert", "app.page" })]
        [InlineData("""{ "type": ["app.alert", "app.*"] }""", new[] { "app.alert" })]
        [InlineData("""{ "type": "*" }""", null)]
        [InlineData("""{ "labels": { "env": "prod" } }""", null)]
        [InlineData("""{ }""", null)]
        public void A_match_names_the_types_it_gives_without_wildcards(string match, string[]? exact)
        {
            var parsed = RouteMatch.Parse(JsonElement.Parse(match)).Match!;
            Assert.Equal(exact?.Order(), parsed.ExactTypes?.Order());
        }

        [Fact]
        public void A_stored_zone_this_host_no_longer_knows_is_shown_as_absent()
        {
            var stored = """{"start":"22:00","end":"07:00","timeZone":"Mars/Olympus_Mons"}""";
            Assert.Equal("Mars/Olympus_Mons", QuietHours.TryRead(stored)!.TimeZone);
            var shown = QuietHours.Shown(stored)!;
            Assert.Null(shown.TimeZone);
            Assert.Empty(PreferenceRules.Problems(shown)); // the person can send back what they see
            Assert.Equal("Europe/Berlin", QuietHours.Shown("""{"start":"22:00","end":"07:00","timeZone":"Europe/Berlin"}""")!.TimeZone);
            Assert.Null(QuietHours.Shown(null));
        }
    }
}
