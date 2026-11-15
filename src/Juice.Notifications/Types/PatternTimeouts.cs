using System.Text.RegularExpressions;
using Json.Schema;
using Json.Schema.Keywords;

namespace Juice.Notifications.Types
{
    /// <summary>
    /// Gives the regular expressions of a built schema's <c>pattern</c> and <c>patternProperties</c> a short match timeout (docs/deferred.md #6).
    /// JsonSchema.Net compiles them with a fixed 5 s timeout and no hook to change it, so a pattern that backtracks without end (<c>^(a+)+$</c>)
    /// held the request thread, or the broker consumer, up to 5 s for each string it checked.
    /// <list type="bullet">
    /// <item>Each regex is compiled again with the library's own pattern and options, so a pattern means exactly what it did; only the timeout
    /// changes. A match that runs past it throws out of the whole evaluation, and the data is refused (422): a hostile pattern costs about one
    /// timeout per request, not one per string.</item>
    /// <item>Not the non-backtracking engine: it needs the patterns translated out of ECMAScript mode, refuses lookarounds and backreferences,
    /// keeps megabytes of automaton per regex, and still ran for seconds on some patterns without honoring its timeout.</item>
    /// <item>The built tree is walked rather than the dialect replaced: the library orders keywords by handler type, so a subclass handler breaks
    /// the dialect, and a <c>$schema</c> member anywhere in the document switches back to the library's own dialect. Every node the build makes,
    /// a <c>$ref</c> target included, is a subschema of some keyword, so the walk reaches every regex the evaluation will match. No other keyword
    /// matches a declared pattern (<c>additionalProperties</c> reads what <c>patternProperties</c> matched from its annotation).</item>
    /// </list>
    /// </summary>
    internal static class PatternTimeouts
    {
        /// <summary>A pattern on data of at most 64 KB takes well under a millisecond unless it backtracks.</summary>
        internal static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

        public static void Apply(JsonSchema schema)
        {
            var seen = new HashSet<JsonSchemaNode>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<JsonSchemaNode>();
            pending.Push(schema.Root);
            while (pending.TryPop(out var node))
            {
                if (!seen.Add(node))
                {
                    continue;
                }
                foreach (var keyword in node.Keywords)
                {
                    // A library update that keeps its regexes in another shape fails every build here, rather than quietly taking 5 s again.
                    switch (keyword.Handler)
                    {
                        case PatternKeyword:
                            keyword.Value = keyword.Value is Regex regex ? Shorten(regex) : throw Unexpected(keyword);
                            break;
                        case PatternPropertiesKeyword:
                            keyword.Value = keyword.Value is Dictionary<string, Regex> regexes
                                ? regexes.ToDictionary(r => r.Key, r => Shorten(r.Value), StringComparer.Ordinal)
                                : throw Unexpected(keyword);
                            break;
                    }
                    foreach (var subschema in keyword.Subschemas)
                    {
                        pending.Push(subschema);
                    }
                }
            }
        }

        /// <summary>The library's regex with the short timeout. Interpreted: the library's compiled copy already paid for generating code once.</summary>
        internal static Regex Shorten(Regex library) => new(library.ToString(), library.Options & ~RegexOptions.Compiled, Timeout);

        private static InvalidOperationException Unexpected(KeywordData keyword)
            => new($"JsonSchema.Net keeps '{keyword.Handler.Name}' as {keyword.Value?.GetType().Name ?? "null"}; PatternTimeouts expects its regexes.");
    }
}
