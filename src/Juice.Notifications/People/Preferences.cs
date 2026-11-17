using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.MediatR;
using Juice.Notifications.Data;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.People
{
    // Preferences, quiet hours and mutes (design §6.1 step 4, §6.4, phase-2-plan M14, I21): what a person (or their tenant) chose not to hear,
    // and how the mandatory policy bends it. The planner decides whether someone hears (tenant mutes, category opt-out, personal mutes); the
    // delivery planner decides how (channels turned off, quiet hours).

    public sealed record PreferenceEntry(string? Category, string? Channel, string? Mode);

    public sealed record PreferenceView(string Category, string Channel, string Mode, DateTimeOffset UpdatedAt);

    /// <summary>Quiet hours as the person gives them: the days a quiet period starts on, its local start and end (<c>HH:mm</c>), and its time zone.</summary>
    public sealed record QuietHoursSpec(IReadOnlyList<string>? Days, string? Start, string? End, string? TimeZone);

    /// <summary>
    /// A mute. <see cref="AppliesToMandatory"/>: a tenant mute naming at least one mandatory type exactly, so it holds that type too (design §6.4);
    /// the Console flags it. A personal mute never holds a mandatory type.
    /// </summary>
    public sealed record MuteView(Guid Id, Guid? SubscriberId, JsonElement Match, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, string? Reason,
        string? CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? CancelledAt, bool AppliesToMandatory);

    /// <summary>Replaces the person's preferences with these (an empty list: everything on).</summary>
    public sealed record PutPreferences(Guid NotificationTenantId, Guid SubscriberId, IReadOnlyList<PreferenceEntry>? Preferences)
        : MessageBase, IRequest<Outcome<IReadOnlyList<PreferenceView>>>;

    /// <summary>Sets the person's quiet hours, or clears them (null).</summary>
    public sealed record SetQuietHours(Guid NotificationTenantId, Guid SubscriberId, QuietHoursSpec? QuietHours) : MessageBase, IRequest<Outcome<QuietHoursSpec?>>;

    /// <summary>
    /// Adds a mute: the tenant's (<see cref="SubscriberId"/> null), or a person's own. A match of <c>{}</c> (or none) mutes everything, which a
    /// snooze is; <see cref="EndsAt"/> null lasts until cancelled.
    /// </summary>
    public sealed record AddMute(Guid NotificationTenantId, Guid? SubscriberId, JsonElement? Match, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
        string? Reason, string? CreatedBy) : MessageBase, IRequest<Outcome<MuteView>>;

    /// <summary>Cancels a mute: the tenant's (<see cref="SubscriberId"/> null), or the person's own. One already cancelled stays as it was.</summary>
    public sealed record CancelMute(Guid NotificationTenantId, Guid? SubscriberId, Guid MuteId) : MessageBase, IRequest<Outcome<MuteView>>;

    public static partial class PreferenceRules
    {
        public const string Any = "*";
        public const int MaxPreferences = 200;
        public const int MaxPersonalMutes = 100;
        public const int MaxTenantMutes = 500;
        public const int MaxReason = 500;
        public static readonly TimeSpan MaxMuteLength = TimeSpan.FromDays(366);

        /// <summary>A mute listing's page size, at most.</summary>
        public const int MaxPage = 200;

        /// <summary>A type's category (as types declare it), or <c>*</c>.</summary>
        public static bool IsCategory(string category) => category == Any || Types.TypeRules.IsCategory(category);

        [GeneratedRegex(@"^([01][0-9]|2[0-3]):[0-5][0-9]\z")]
        private static partial Regex Time();

        public static readonly IReadOnlyList<string> DayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

        private static readonly Dictionary<string, DayOfWeek> Days = new(StringComparer.Ordinal)
        {
            ["mon"] = DayOfWeek.Monday, ["tue"] = DayOfWeek.Tuesday, ["wed"] = DayOfWeek.Wednesday, ["thu"] = DayOfWeek.Thursday,
            ["fri"] = DayOfWeek.Friday, ["sat"] = DayOfWeek.Saturday, ["sun"] = DayOfWeek.Sunday,
        };

        internal static DayOfWeek Day(string name) => Days[name];

        /// <summary>Channels a preference can name: <c>*</c>, the inbox, and channels that reach people at an address.</summary>
        public static bool IsChannel(string channel, IChannelRegistry channels)
            => channel is Any or Notifications.Channels.Inbox || ContactRules.TakesContactPoints(channels.Find(channel));

        /// <summary>
        /// The problems with quiet hours, as JSON pointers under <c>/quietHours</c>. <paramref name="checkZone"/>: whether the time zone must be
        /// one this host knows (when saved); a stored one it no longer knows reads as the person's, else UTC.
        /// </summary>
        public static List<FieldError> Problems(QuietHoursSpec q, bool checkZone = true)
        {
            var errors = new List<FieldError>();
            if (q.Days is { } days && (days.Count == 0 || days.Any(d => d is null || !DayNames.Contains(d)) || days.Distinct().Count() != days.Count))
            {
                errors.Add(new FieldError("/quietHours/days", "Days are distinct names from mon, tue, wed, thu, fri, sat, sun (left out: every day)."));
            }
            if (q.Start is null || !Time().IsMatch(q.Start))
            {
                errors.Add(new FieldError("/quietHours/start", "The start is a local time, HH:mm."));
            }
            if (q.End is null || !Time().IsMatch(q.End))
            {
                errors.Add(new FieldError("/quietHours/end", "The end is a local time, HH:mm."));
            }
            else if (q.Start == q.End)
            {
                errors.Add(new FieldError("/quietHours/end", "The end differs from the start; an end before the start runs past midnight."));
            }
            if (checkZone && q.TimeZone is not null && !DirectoryRules.IsTimeZone(q.TimeZone))
            {
                errors.Add(new FieldError("/quietHours/timeZone", "A time zone is an IANA id such as Asia/Ho_Chi_Minh (left out: the person's, else UTC)."));
            }
            return errors;
        }
    }

    /// <summary>
    /// Quiet hours (design §6.4, phase-2-plan I21): periods that start on given days at a local time and end at a local time (the next day when the
    /// end is earlier), in the person's time zone. Wall-clock times, so a period keeps its hours across a DST change.
    /// </summary>
    public sealed class QuietHours
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly bool[] _days = new bool[7]; // by DayOfWeek
        private readonly TimeSpan _start;
        private readonly TimeSpan _end;

        public string? TimeZone { get; }

        private QuietHours(QuietHoursSpec spec)
        {
            foreach (var day in spec.Days ?? PreferenceRules.DayNames)
            {
                _days[(int)PreferenceRules.Day(day)] = true;
            }
            (_start, _end, TimeZone) = (TimeSpan.Parse(spec.Start!), TimeSpan.Parse(spec.End!), spec.TimeZone);
        }

        /// <summary>Quiet hours from what the person gave, checked; null with the problems otherwise.</summary>
        public static (QuietHours? Hours, List<FieldError> Errors) From(QuietHoursSpec spec)
            => PreferenceRules.Problems(spec) is { Count: > 0 } errors ? (null, errors) : (new QuietHours(spec), []);

        /// <summary>The stored form: canonical, days in week order (every day when all seven).</summary>
        public static string Store(QuietHoursSpec spec)
        {
            var days = spec.Days is null || spec.Days.Count == 7 ? null : PreferenceRules.DayNames.Where(spec.Days.Contains).ToList();
            return JsonSerializer.Serialize(new QuietHoursSpec(days, spec.Start, spec.End, spec.TimeZone), Json);
        }

        public static QuietHoursSpec? Read(string? stored) => stored is null ? null : JsonSerializer.Deserialize<QuietHoursSpec>(stored, Json);


        /// <summary>
        /// Stored quiet hours, or null when there are none or they don't hold (a value changed by hand): exactly those <see cref="Parse"/> applies,
        /// so what the person sees is what planning does, and they can set them again.
        /// </summary>
        public static QuietHoursSpec? TryRead(string? stored)
        {
            try
            {
                return Read(stored) is { } spec && PreferenceRules.Problems(spec, checkZone: false).Count == 0 ? spec : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Stored quiet hours, or null when there are none or they don't hold (<see cref="TryRead"/>). The time zone isn't looked up here (on
        /// every person's delivery planning step): one this host no longer knows reads as the person's, else UTC.
        /// </summary>
        public static QuietHours? Parse(string? stored) => TryRead(stored) is { } spec ? new QuietHours(spec) : null;

        /// <summary>
        /// Stored quiet hours as the person is shown them: as <see cref="TryRead"/>, with a time zone this host no longer knows left out, as
        /// planning treats it, so the person can send back what they see.
        /// </summary>
        public static QuietHoursSpec? Shown(string? stored)
            => TryRead(stored) is not { } spec ? null
                : spec.TimeZone is { } zone && !DirectoryRules.IsTimeZone(zone) ? spec with { TimeZone = null } : spec;


        /// <summary>
        /// When the quiet period <paramref name="now"/> falls in ends, or null when it falls in none. The zone is the quiet hours' own, else the
        /// person's, else UTC. A period runs from the first moment its start shows on the clock to the first moment after that its end does: a
        /// time the clocks skip (a DST gap) is when they jump, and one they show twice (the clocks go back) counts the first time.
        /// </summary>
        public DateTimeOffset? EndIfQuiet(DateTimeOffset now, string? personZone)
        {
            var zone = Zone(TimeZone) ?? Zone(personZone) ?? TimeZoneInfo.Utc;
            var today = TimeZoneInfo.ConvertTime(now, zone).Date;
            // A period that started yesterday may run past midnight into today; one starting tomorrow can't have begun (no shift exceeds a day).
            for (var day = today.AddDays(-1); day <= today; day = day.AddDays(1))
            {
                if (!_days[(int)day.DayOfWeek])
                {
                    continue;
                }
                var start = First(day + _start, zone, DateTimeOffset.MinValue);
                var end = First((_end > _start ? day : day.AddDays(1)) + _end, zone, start);
                if (start <= now && now < end)
                {
                    return end;
                }
            }
            return null;
        }

        private static TimeZoneInfo? Zone(string? id) => id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

        /// <summary>
        /// The first instant after <paramref name="after"/> at which the clock in the zone shows <paramref name="wall"/>; when it never shows it
        /// (a DST gap), the instant the clocks jump past it. A wall time is at most two instants, a few hours apart, so "after" only chooses
        /// between those.
        /// </summary>
        internal static DateTimeOffset First(DateTime wall, TimeZoneInfo zone, DateTimeOffset after)
        {
            wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(wall))
            {
                // Gaps fall on whole minutes: the first valid minute after it is the jump.
                var step = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, wall.Minute, 0, DateTimeKind.Unspecified);
                do
                {
                    step = step.AddMinutes(1);
                }
                while (zone.IsInvalidTime(step));
                return new DateTimeOffset(step, zone.GetUtcOffset(step));
            }
            if (zone.IsAmbiguousTime(wall))
            {
                var candidates = zone.GetAmbiguousTimeOffsets(wall).Select(o => new DateTimeOffset(wall, o)).Order().ToList();
                return candidates.FirstOrDefault(c => c > after, candidates[^1]);
            }
            return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        }
    }

    /// <summary>
    /// Parsed mute matches, by mute (a mute's match never changes; ids are never reused). Past <see cref="MaxCached"/> a tenth is dropped, not
    /// all, so a deployment with many mutes doesn't rebuild every match on the next plan.
    /// </summary>
    public sealed class MuteMatches
    {
        public const int MaxCached = 50_000;
        private readonly ConcurrentDictionary<Guid, RouteMatch?> _parsed = new();

        /// <summary>The mute's match, or null when it no longer parses (it was checked when added).</summary>
        public RouteMatch? Get(Mute mute)
        {
            if (_parsed.TryGetValue(mute.Id, out var match))
            {
                return match;
            }
            if (_parsed.Count >= MaxCached)
            {
                // The oldest mutes first: ids are UUIDv7, and an old mute is the likeliest to have ended.
                foreach (var id in _parsed.Keys.Order().Take(MaxCached / 10).ToList())
                {
                    _parsed.TryRemove(id, out _);
                }
            }
            return _parsed[mute.Id] = RouteMatch.Parse(JsonElement.Parse(mute.Match)).Match;
        }
    }

    /// <summary>The mandatory policy's rules, applied where design §6.4 puts them (phase-2-plan I21).</summary>
    public static class PreferencePolicy
    {
        /// <summary>Mutes in force at <paramref name="now"/>: started, not ended, not cancelled.</summary>
        internal static IQueryable<Mute> Active(this IQueryable<Mute> mutes, DateTimeOffset now)
            => mutes.Where(m => m.CancelledAt == null && m.StartsAt <= now && (m.EndsAt == null || m.EndsAt > now));

        /// <summary>
        /// Applies tenant mutes, then category opt-out, then personal mutes to a plan's recipients (design §6.1 step 4), setting the outcome of
        /// each one that won't hear (<c>Muted</c>, <c>OptedOut</c>; no longer due) and recording on the others what a mandatory type went past.
        /// A tenant mute holds every kind of recipient; preferences and personal mutes hold people. A mandatory type is held only by a tenant
        /// mute naming it exactly. The service's own types (a verification code) are never muted: a tenant can't stop its people verifying.
        /// </summary>
        public static async Task ApplyAsync(NotificationsDbContext db, NotificationRequest request, NotificationType type, IReadOnlyList<NotificationRecipient> recipients,
            MuteMatches matches, DateTimeOffset now, CancellationToken ct)
        {
            if (recipients.Count == 0 || ServiceTypes.IsServiceType(type.Key))
            {
                return;
            }
            bool Matches(Mute mute) => matches.Get(mute)?.Matches(type.Key, request.Labels, request.Priority) == true;

            var tenantMutes = (await db.Mutes.AsNoTracking().Where(m => m.TenantId == request.TenantId && m.SubscriberId == null).Active(now).ToListAsync(ct))
                .Where(Matches).ToList();
            var tenantHolds = type.Mandatory
                ? tenantMutes.Any(m => matches.Get(m)?.ExactTypes?.Contains(type.Key) == true)
                : tenantMutes.Count > 0;
            foreach (var recipient in recipients)
            {
                if (tenantHolds)
                {
                    Hold(recipient, RecipientOutcome.Muted);
                }
                else if (tenantMutes.Count > 0)
                {
                    Bypass(recipient, BypassedRules.TenantMute);
                }
            }

            var people = recipients.Where(r => r.Outcome == RecipientOutcome.Planned && r.SubscriberId is not null).ToList();
            if (people.Count == 0)
            {
                return;
            }
            var ids = people.Select(r => r.SubscriberId!.Value).ToArray();
            var optOuts = (await db.Preferences.AsNoTracking()
                .Where(p => p.TenantId == request.TenantId && ids.Contains(p.SubscriberId) && (p.Category == type.Category || p.Category == PreferenceRules.Any))
                .ToListAsync(ct))
                .GroupBy(p => p.SubscriberId)
                .Where(g => OptedOut(g.ToList(), type.Category))
                .Select(g => g.Key).ToHashSet();
            var personalMutes = (await db.Mutes.AsNoTracking().Where(m => m.TenantId == request.TenantId && m.SubscriberId != null && ids.Contains(m.SubscriberId.Value))
                .Active(now).ToListAsync(ct))
                .Where(Matches).Select(m => m.SubscriberId!.Value).ToHashSet();
            foreach (var person in people)
            {
                // Opt-out first, then a personal mute: a normal type stops at the first that applies; a mandatory one records both.
                var id = person.SubscriberId!.Value;
                if (optOuts.Contains(id))
                {
                    Apply(person, type.Mandatory, BypassedRules.OptOut, RecipientOutcome.OptedOut);
                }
                if (personalMutes.Contains(id) && person.Outcome == RecipientOutcome.Planned)
                {
                    Apply(person, type.Mandatory, BypassedRules.PersonalMute, RecipientOutcome.Muted);
                }
            }
        }

        private static void Apply(NotificationRecipient person, bool mandatory, string rule, RecipientOutcome outcome)
        {
            if (mandatory)
            {
                Bypass(person, rule);
            }
            else
            {
                Hold(person, outcome);
            }
        }

        private static void Hold(NotificationRecipient recipient, RecipientOutcome outcome)
        {
            recipient.Outcome = outcome;
            recipient.NextAt = null;
        }

        internal static void Bypass(NotificationRecipient recipient, string rule)
        {
            if (!recipient.Bypassed.Contains(rule))
            {
                recipient.Bypassed = [.. recipient.Bypassed, rule];
            }
        }

        /// <summary>
        /// A person's mode for a category on a channel (design §6.4): the most specific of their preferences, the category before every category
        /// (<c>*</c>), then the channel before every channel; on when none applies. <paramref name="channelOnly"/> reads only choices for the
        /// channel itself: what a mandatory type keeps of them, having gone past the category's choice for every channel (the opt-out).
        /// </summary>
        public static string Mode(IReadOnlyCollection<Preference> preferences, string category, string channel, bool channelOnly = false)
        {
            foreach (var (c, ch) in new[] { (category, channel), (category, PreferenceRules.Any), (PreferenceRules.Any, channel), (PreferenceRules.Any, PreferenceRules.Any) })
            {
                if (channelOnly && ch == PreferenceRules.Any)
                {
                    continue;
                }
                if (preferences.FirstOrDefault(p => p.Category == c && p.Channel == ch) is { } chosen)
                {
                    return chosen.Mode;
                }
            }
            return PreferenceModes.On;
        }

        /// <summary>
        /// Whether a person opted out of a category: it is off on every channel, those they named and the rest (<c>*</c>). One channel turned back
        /// on (<c>* * off</c> with <c>billing email on</c>) keeps the category, on that channel.
        /// </summary>
        public static bool OptedOut(IReadOnlyCollection<Preference> preferences, string category)
            => Mode(preferences, category, PreferenceRules.Any) == PreferenceModes.Off
                && preferences.Where(p => p.Channel != PreferenceRules.Any).Select(p => p.Channel).Distinct()
                    .All(channel => Mode(preferences, category, channel) == PreferenceModes.Off);
    }

    internal sealed class PreferenceHandlers(NotificationsDbContext db, IChannelRegistry channels, TimeProvider clock, MuteMatches matches) :
        IRequestHandler<PutPreferences, Outcome<IReadOnlyList<PreferenceView>>>, IRequestHandler<SetQuietHours, Outcome<QuietHoursSpec?>>,
        IRequestHandler<AddMute, Outcome<MuteView>>, IRequestHandler<CancelMute, Outcome<MuteView>>
    {
        private static Outcome<T> Invalid<T>(IReadOnlyList<FieldError> errors) => Outcome<T>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.", errors);

        public async ValueTask<Outcome<IReadOnlyList<PreferenceView>>> Handle(PutPreferences r, CancellationToken ct)
        {
            if (r.Preferences is not { } given)
            {
                // Left out is a mistake, not "everything on": it would delete every choice the person made.
                return Invalid<IReadOnlyList<PreferenceView>>([new FieldError("/preferences", "The preferences are a list (an empty one turns everything on).")]);
            }
            var errors = new List<FieldError>();
            if (given.Count > PreferenceRules.MaxPreferences)
            {
                errors.Add(new FieldError("/preferences", $"Up to {PreferenceRules.MaxPreferences} preferences."));
            }
            for (var i = 0; i < given.Count && errors.Count < 20; i++)
            {
                var p = given[i];
                if (p is null)
                {
                    errors.Add(new FieldError($"/preferences/{i}", "A preference is an object: category, channel, mode."));
                    continue;
                }
                if (p.Category is null || !PreferenceRules.IsCategory(p.Category))
                {
                    errors.Add(new FieldError($"/preferences/{i}/category", "The category is a type's category, or * for all of them."));
                }
                if (p.Channel is null || !PreferenceRules.IsChannel(p.Channel, channels))
                {
                    var known = string.Join(", ", channels.All.Where(ContactRules.TakesContactPoints).Select(c => c.Key).Order(StringComparer.Ordinal));
                    errors.Add(new FieldError($"/preferences/{i}/channel", $"The channel is *, inbox or one of: {known}."));
                }
                if (p.Mode is null || !PreferenceModes.All.Contains(p.Mode))
                {
                    errors.Add(new FieldError($"/preferences/{i}/mode", "The mode is on, off or digest."));
                }
            }
            if (errors.Count == 0 && given.GroupBy(p => (p.Category, p.Channel)).FirstOrDefault(g => g.Count() > 1) is { } twice)
            {
                errors.Add(new FieldError("/preferences", $"The category {twice.Key.Category} on channel {twice.Key.Channel} is given twice."));
            }
            if (errors.Count > 0)
            {
                return Invalid<IReadOnlyList<PreferenceView>>(errors);
            }
            // Changed row by row, so each change is audited and an unchanged one keeps its time.
            var now = clock.UtcNow();
            var existing = await db.Preferences.Where(p => p.TenantId == r.NotificationTenantId && p.SubscriberId == r.SubscriberId).ToListAsync(ct);
            var wanted = given.ToDictionary(p => (p.Category!, p.Channel!), p => p.Mode!);
            foreach (var row in existing)
            {
                if (!wanted.Remove((row.Category, row.Channel), out var mode))
                {
                    db.Preferences.Remove(row);
                }
                else if (row.Mode != mode)
                {
                    row.Mode = mode;
                    row.UpdatedAt = now;
                }
            }
            foreach (var ((category, channel), mode) in wanted)
            {
                var row = new Preference { TenantId = r.NotificationTenantId, SubscriberId = r.SubscriberId, Category = category, Channel = channel, Mode = mode, UpdatedAt = now };
                db.Preferences.Add(row);
                existing.Add(row);
            }
            return Outcome<IReadOnlyList<PreferenceView>>.Ok(PreferenceReader.Views(existing.Where(p => db.Entry(p).State != EntityState.Deleted)));
        }

        public async ValueTask<Outcome<QuietHoursSpec?>> Handle(SetQuietHours r, CancellationToken ct)
        {
            string? stored = null;
            if (r.QuietHours is { } spec)
            {
                if (QuietHours.From(spec).Errors is { Count: > 0 } errors)
                {
                    return Invalid<QuietHoursSpec?>(errors);
                }
                stored = QuietHours.Store(spec);
            }
            var subscriber = await db.Subscribers.SingleOrDefaultAsync(s => s.TenantId == r.NotificationTenantId && s.Id == r.SubscriberId, ct);
            if (subscriber is null)
            {
                return Outcome<QuietHoursSpec?>.NotFound("No such subscriber.");
            }
            subscriber.QuietHours = stored;
            return Outcome<QuietHoursSpec?>.Ok(QuietHours.Read(stored));
        }

        public async ValueTask<Outcome<MuteView>> Handle(AddMute r, CancellationToken ct)
        {
            var now = clock.UtcNow();
            var errors = new List<FieldError>();
            var match = r.Match is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } m ? m : JsonElement.Parse("{}");
            // jsonb can't hold a NUL or half a surrogate pair: refused here, as a route's match is, rather than failing the save.
            var (parsed, matchErrors) = CanonicalJson.Unstorable(match, "/match") is { } unstorable ? (null, [unstorable]) : RouteMatch.Parse(match);
            errors.AddRange(matchErrors);
            if (parsed?.ExactTypes?.FirstOrDefault(ServiceTypes.IsServiceType) is { } own)
            {
                // Never muted anyway (a tenant can't stop its people verifying an address); refused so the mute doesn't claim otherwise.
                errors.Add(new FieldError("/match/type", $"{own} is the service's own type, which mutes don't hold."));
            }
            var starts = r.StartsAt ?? now;
            if (r.StartsAt is { } s && (s < now.AddMinutes(-5) || s > now + PreferenceRules.MaxMuteLength))
            {
                errors.Add(new FieldError("/startsAt", "A mute starts now or later, within a year (left out: now)."));
            }
            if (r.EndsAt is { } e && (e <= starts || e <= now || e - starts > PreferenceRules.MaxMuteLength))
            {
                errors.Add(new FieldError("/endsAt", "A mute ends after it starts and after now, within a year of starting (left out: until cancelled)."));
            }
            if (r.Reason is { } reason && (reason.Length > PreferenceRules.MaxReason || reason.Any(char.IsControl) || !Intake.RequestRules.IsWellFormed(reason)))
            {
                errors.Add(new FieldError("/reason", $"A reason is up to {PreferenceRules.MaxReason} characters, no control characters."));
            }
            if (errors.Count > 0)
            {
                return Invalid<MuteView>(errors);
            }
            var active = await db.Mutes.AsNoTracking().CountAsync(x => x.TenantId == r.NotificationTenantId && x.SubscriberId == r.SubscriberId
                && x.CancelledAt == null && (x.EndsAt == null || x.EndsAt > now), ct);
            var max = r.SubscriberId is null ? PreferenceRules.MaxTenantMutes : PreferenceRules.MaxPersonalMutes;
            if (active >= max)
            {
                return Outcome<MuteView>.Conflict(ErrorCodes.MuteLimit, $"There are {max} mutes in force or to come; cancel one first.");
            }
            var mute = new Mute
            {
                TenantId = r.NotificationTenantId, SubscriberId = r.SubscriberId, Match = match.GetRawText(), StartsAt = starts, EndsAt = r.EndsAt,
                Reason = r.Reason, CreatedBy = r.CreatedBy is { Length: > 200 } by ? by[..200] : r.CreatedBy, CreatedAt = now, // the column's length
            };
            db.Mutes.Add(mute);
            return Outcome<MuteView>.Created(await PreferenceReader.ViewAsync(db, matches, mute, ct));
        }

        public async ValueTask<Outcome<MuteView>> Handle(CancelMute r, CancellationToken ct)
        {
            var mute = await db.Mutes.SingleOrDefaultAsync(m => m.TenantId == r.NotificationTenantId && m.Id == r.MuteId && m.SubscriberId == r.SubscriberId, ct);
            if (mute is null)
            {
                return Outcome<MuteView>.NotFound("No such mute.");
            }
            mute.CancelledAt ??= clock.UtcNow();
            return Outcome<MuteView>.Ok(await PreferenceReader.ViewAsync(db, matches, mute, ct));
        }
    }

    /// <summary>Reads preferences, quiet hours and mutes, outside any command.</summary>
    public sealed class PreferenceReader(NotificationsDbContext db, TimeProvider clock, MuteMatches matches)
    {
        internal static IReadOnlyList<PreferenceView> Views(IEnumerable<Preference> rows)
            => rows.OrderBy(p => p.Category, StringComparer.Ordinal).ThenBy(p => p.Channel, StringComparer.Ordinal)
                .Select(p => new PreferenceView(p.Category, p.Channel, p.Mode, p.UpdatedAt)).ToList();

        public async Task<IReadOnlyList<PreferenceView>> PreferencesAsync(Guid tenantId, Guid subscriberId, CancellationToken ct)
            => Views(await db.Preferences.AsNoTracking().Where(p => p.TenantId == tenantId && p.SubscriberId == subscriberId).ToListAsync(ct));

        /// <summary>
        /// A page of the tenant's mutes (<paramref name="subscriberId"/> null) or a person's, newest first (by id: UUIDv7, so creation order),
        /// before <paramref name="before"/>: those in force or to come, or all of them (ended and cancelled ones are kept).
        /// </summary>
        public async Task<IReadOnlyList<MuteView>> MutesAsync(Guid tenantId, Guid? subscriberId, bool all, Guid? before, int limit, CancellationToken ct)
        {
            var now = clock.UtcNow();
            var query = db.Mutes.AsNoTracking().Where(m => m.TenantId == tenantId && m.SubscriberId == subscriberId);
            if (!all)
            {
                query = query.Where(m => m.CancelledAt == null && (m.EndsAt == null || m.EndsAt > now));
            }
            if (before is { } cursor)
            {
                query = query.Where(m => m.Id.CompareTo(cursor) < 0);
            }
            var mutes = await query.OrderByDescending(m => m.Id).Take(Math.Clamp(limit, 1, PreferenceRules.MaxPage)).ToListAsync(ct);
            var mandatory = await MandatoryAsync(db, matches, mutes, ct);
            return mutes.Select(m => View(m, mandatory)).ToList();
        }

        internal static async Task<MuteView> ViewAsync(NotificationsDbContext db, MuteMatches matches, Mute mute, CancellationToken ct)
            => View(mute, await MandatoryAsync(db, matches, [mute], ct));

        private static MuteView View(Mute m, IReadOnlySet<Guid> mandatory)
            => new(m.Id, m.SubscriberId, JsonElement.Parse(m.Match), m.StartsAt, m.EndsAt, m.Reason, m.CreatedBy, m.CreatedAt, m.CancelledAt, mandatory.Contains(m.Id));

        /// <summary>The tenant mutes among these that name a mandatory type exactly (any version of it mandatory).</summary>
        private static async Task<IReadOnlySet<Guid>> MandatoryAsync(NotificationsDbContext db, MuteMatches matches, IEnumerable<Mute> mutes, CancellationToken ct)
        {
            var named = mutes.Where(m => m.SubscriberId is null).Select(m => (m.Id, Types: matches.Get(m)?.ExactTypes))
                .Where(x => x.Types is { Count: > 0 }).ToList();
            if (named.Count == 0)
            {
                return new HashSet<Guid>();
            }
            var keys = named.SelectMany(x => x.Types!).Distinct().ToArray();
            var mandatory = (await db.Types.AsNoTracking().Where(t => keys.Contains(t.Key) && t.Mandatory).Select(t => t.Key).Distinct().ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            return named.Where(x => x.Types!.Overlaps(mandatory)).Select(x => x.Id).ToHashSet();
        }
    }
}
