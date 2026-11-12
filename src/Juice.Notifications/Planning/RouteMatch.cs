using System.Text.Json;
using System.Text.RegularExpressions;

namespace Juice.Notifications.Planning
{
    /// <summary>
    /// What a route matches (design §6.3): type patterns (<c>*</c> matches anything, dots included), label matchers, and a minimum priority.
    /// Everything given must match. A label matcher is <c>"value"</c> or <c>"=value"</c> (equals), <c>"!=value"</c> (differs, or the label is
    /// missing), <c>"=~regex"</c> (the whole value matches) or <c>"in(a,b,c)"</c> (one of).
    /// <para>
    /// Every pattern runs on .NET's non-backtracking engine: matching takes time linear in the value, whatever the pattern, so one tenant's
    /// route can't slow planning for everyone. Backreferences and lookarounds aren't available there and are refused when the route is saved.
    /// </para>
    /// </summary>
    public sealed partial class RouteMatch
    {
        private const RegexOptions Engine = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        public const int MaxLabelMatchers = 50;
        public const int MaxPatternLength = 500;
        /// <summary>
        /// Building a non-backtracking regex can cost far more than matching with it (counted repetitions expand), so a route has few and short
        /// ones: building a route's matchers stays in the milliseconds, at save and when the planner first reads it.
        /// </summary>
        public const int MaxRegexMatchers = 10;
        public const int MaxRegexLength = 200;

        private readonly Regex[] _types;
        private readonly (string Label, Func<string?, bool> Test)[] _labels;

        public NotificationPriority? MinPriority { get; }

        private RouteMatch(Regex[] types, (string, Func<string?, bool>)[] labels, NotificationPriority? minPriority)
            => (_types, _labels, MinPriority) = (types, labels, minPriority);

        public bool Matches(string typeKey, IReadOnlyDictionary<string, string> labels, NotificationPriority priority)
            => (_types.Length == 0 || _types.Any(t => t.IsMatch(typeKey)))
            && _labels.All(m => m.Test(labels.GetValueOrDefault(m.Label)))
            && (MinPriority is not { } min || priority >= min);

        [GeneratedRegex(@"^[a-z0-9*][a-z0-9_.*-]{0,199}\z")]
        private static partial Regex TypePattern();

        /// <summary>An inline option group that turns on free-spacing (<c>x</c>): <c>(?x)</c>, <c>(?ix:…)</c>.</summary>
        [GeneratedRegex(@"\(\?[imns]*x[imnsx]*(-[imnsx]*)?[:)]")]
        private static partial Regex FreeSpacing();

        /// <summary>Parses a route's match, or reports what's wrong with it (JSON pointers under <c>/match</c>).</summary>
        public static (RouteMatch? Match, IReadOnlyList<FieldError> Errors) Parse(JsonElement match)
        {
            var errors = new List<FieldError>();
            if (match.ValueKind != JsonValueKind.Object)
            {
                return (null, [new FieldError("/match", "The match is an object: type, labels, minPriority.")]);
            }
            var types = new List<Regex>();
            var labels = new List<(string, Func<string?, bool>)>();
            NotificationPriority? min = null;
            foreach (var property in match.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "type":
                        var patterns = property.Value.ValueKind switch
                        {
                            JsonValueKind.String => [property.Value.GetString()!],
                            JsonValueKind.Array when property.Value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String)
                                => property.Value.EnumerateArray().Select(v => v.GetString()!).ToList(),
                            _ => null,
                        };
                        if (patterns is null || patterns.Count is 0 or > 20 || patterns.Any(p => !TypePattern().IsMatch(p)))
                        {
                            errors.Add(new FieldError("/match/type", "The type is a pattern or a list of up to 20: lowercase type keys, * for any run of characters."));
                            break;
                        }
                        types.AddRange(patterns.Select(p => new Regex("^" + string.Join(".*", p.Split('*').Select(Regex.Escape)) + @"\z", Engine)));
                        break;
                    case "labels":
                        if (property.Value.ValueKind != JsonValueKind.Object)
                        {
                            errors.Add(new FieldError("/match/labels", "Labels map a label name to a matcher."));
                            break;
                        }
                        if (property.Value.EnumerateObject().Count() > MaxLabelMatchers)
                        {
                            errors.Add(new FieldError("/match/labels", $"Up to {MaxLabelMatchers} label matchers."));
                            break;
                        }
                        var regexes = property.Value.EnumerateObject().Where(l => l.Value.ValueKind == JsonValueKind.String && l.Value.GetString()!.StartsWith("=~", StringComparison.Ordinal)).ToList();
                        if (regexes.Count > MaxRegexMatchers || regexes.Any(l => l.Value.GetString()!.Length - 2 > MaxRegexLength))
                        {
                            errors.Add(new FieldError("/match/labels", $"Up to {MaxRegexMatchers} =~ matchers, each up to {MaxRegexLength} characters."));
                            break;
                        }
                        foreach (var label in property.Value.EnumerateObject())
                        {
                            var at = "/match/labels/" + Types.TypeRules.Pointer(label.Name);
                            if (label.Value.ValueKind != JsonValueKind.String || label.Value.GetString()!.Length > MaxPatternLength)
                            {
                                errors.Add(new FieldError(at, $"A label matcher is a string of up to {MaxPatternLength} characters: value, =value, !=value, =~regex or in(a,b)."));
                            }
                            else if (Matcher(label.Value.GetString()!) is { } test)
                            {
                                labels.Add((label.Name, test));
                            }
                            else
                            {
                                errors.Add(new FieldError(at, "The regular expression doesn't compile, or uses a backreference, a lookaround or the inline x option, which routes don't support."));
                            }
                        }
                        break;
                    case "minPriority":
                        if (property.Value.ValueKind != JsonValueKind.String || !Types.TypeRules.TryPriority(property.Value.GetString()!, out var p))
                        {
                            errors.Add(new FieldError("/match/minPriority", "The minimum priority is low, normal, high or critical."));
                            break;
                        }
                        min = p;
                        break;
                    default:
                        errors.Add(new FieldError("/match/" + Types.TypeRules.Pointer(property.Name), "A match has type, labels and minPriority only."));
                        break;
                }
            }
            return errors.Count > 0 ? (null, errors) : (new RouteMatch([.. types], [.. labels], min), []);
        }

        /// <summary>A label value test, or null when a regular expression doesn't compile on the non-backtracking engine.</summary>
        private static Func<string?, bool>? Matcher(string matcher)
        {
            if (matcher.StartsWith("!=", StringComparison.Ordinal))
            {
                var value = matcher[2..];
                return actual => actual != value;
            }
            if (matcher.StartsWith("=~", StringComparison.Ordinal))
            {
                var pattern = matcher[2..];
                // Inline free-spacing mode turns '#' into a comment that would swallow the anchors added around the pattern.
                if (FreeSpacing().IsMatch(pattern))
                {
                    return null;
                }
                Regex regex;
                try
                {
                    // The pattern must compile on its own first: one that closes the group itself ("x)|(?:.*") would otherwise escape the
                    // anchors around it and match any value. The default engine checks that cheaply; only the anchored regex is built on the
                    // non-backtracking one, which also refuses what it can't run (backreferences, lookarounds).
                    _ = new Regex(pattern, RegexOptions.CultureInvariant);
                    regex = new Regex("^(?:" + pattern + @")\z", Engine);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    return null;
                }
                return actual => actual is not null && regex.IsMatch(actual);
            }
            if (matcher.StartsWith("in(", StringComparison.Ordinal) && matcher.EndsWith(')'))
            {
                var values = matcher[3..^1].Split(',').Select(v => v.Trim()).ToHashSet(StringComparer.Ordinal);
                return actual => actual is not null && values.Contains(actual);
            }
            // "=value" is the design's spelling of equals; a bare value means the same.
            var expected = matcher.StartsWith('=') ? matcher[1..] : matcher;
            return actual => actual == expected;
        }
    }
}
