using System.Net;
using System.Text.Json;
using Scriban;
using Scriban.Functions;
using Scriban.Runtime;

namespace Juice.Notifications.Rendering
{
    /// <summary>What a template sees (design §8.2): read-only, and only this. Recipient name and locale join in phase 2, digest items in phase 3.</summary>
    public sealed record RenderModel(string Type, int TypeVersion, string TypeName, NotificationPriority Priority, JsonElement Data,
        IReadOnlyDictionary<string, string> Labels, IReadOnlyDictionary<string, string> Links);

    /// <summary>
    /// A render that failed: the delivery fails as <c>Content</c> (<c>render_failed</c>), without failover. <see cref="Retryable"/> when it ran out
    /// of time, which can be the machine's load rather than the template: the step is tried again instead.
    /// </summary>
    public sealed class RenderException(string message, bool retryable = false) : Exception(message)
    {
        public bool Retryable { get; } = retryable;
    }

    /// <summary>
    /// The one Scriban sandbox every template runs in (design §8.2, D18, D22):
    /// <list type="bullet">
    /// <item>built-ins on an explicit allowlist only (<c>string</c>, <c>array</c>, <c>math</c>, <c>html</c>, and <c>json</c> for building JSON
    /// safely): no <c>include</c> or <c>import</c>, no template loader, nothing that reads the host clock or the environment;</item>
    /// <item>limits: 1,000 loop iterations (Scriban also counts the iterations inside built-ins and ranges against it), recursion 20, template
    /// 64 KB, output 256 KB (padding and string growth are held to it too), and 200 ms of rendering;</item>
    /// <item>a missing field renders empty, never an error.</item>
    /// </list>
    /// Templates are checked at declaration without rendering them (<see cref="Check"/>): a render happens only in a stage worker.
    /// </summary>
    public static class TemplateRenderer
    {
        /// <summary>Recorded on each delivery with the content it produced (design D10).</summary>
        public const string Version = "scriban-7.2.5/1";
        public const int MaxOutputChars = 256 * 1024;
        public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// One render per process before any budget starts: the first render pays for Scriban and the JIT warming up, which can exceed the
        /// budget by itself and fail a valid template, at declaration or at its first delivery after a restart.
        /// </summary>
        private static readonly Lazy<Task> WarmUp = new(WarmAsync);

        private static async Task WarmAsync()
        {
            try
            {
                await RenderCoreAsync("""{{ for x in [1, 2] }}{{ x | string.upcase }}{{ math.round 1.5 }}{{ html.escape data.a }}{{ array.size labels }}{{ end }}""",
                    new RenderModel("t", 1, "t", NotificationPriority.Normal, JsonElement.Parse("""{ "a": "<b>" }"""), new Dictionary<string, string>(),
                        new Dictionary<string, string>()), TimeSpan.FromSeconds(30), CancellationToken.None);
            }
            catch (RenderException)
            {
                // Warming up is best effort; a real render reports its own problems.
            }
        }

        /// <summary>Renders a template; throws <see cref="RenderException"/> for anything the sandbox refuses.</summary>
        public static async Task<string> RenderAsync(string body, RenderModel model, CancellationToken ct)
        {
            await WarmUp.Value.WaitAsync(ct);
            return await RenderCoreAsync(body, model, Budget, ct);
        }

        private static async Task<string> RenderCoreAsync(string body, RenderModel model, TimeSpan limit, CancellationToken ct)
        {
            if (body.Length > MaxTemplateChars)
            {
                throw new RenderException($"A template is at most {MaxTemplateChars / 1024} KB.");
            }
            var template = Template.Parse(body);
            if (template.HasErrors)
            {
                throw new RenderException("The template doesn't parse: " + string.Join("; ", template.Messages.Select(m => m.ToString())));
            }
            // Stored templates (and tenant overrides) don't all come through Check: a chain too deep would overflow the stack and end the process.
            if (TooDeep(template.Page))
            {
                throw new RenderException(DepthMessage);
            }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(limit);
            var context = new TemplateContext(Builtins())
            {
                LoopLimit = 1000,
                RecursiveLimit = 20,
                StrictVariables = false,
                // A missing field renders empty, however deep (data.missing.deeper), never an error.
                EnableRelaxedMemberAccess = true,
                EnableRelaxedTargetAccess = true,
                EnableNullIndexer = true,
                TemplateLoader = null,
                LimitToString = MaxOutputChars,
                CancellationToken = budget.Token,
            };
            context.PushGlobal(Globals(model));
            // Started outside the classifying try: a thread the machine can't create is its problem (a step failure), not the template's.
            var rendering = RenderOnOwnStackAsync(template, context);
            string output;
            try
            {
                output = await rendering;
            }
            // Scriban stops a cancelled render with ScriptAbortException, not OperationCanceledException: tell the caller stopping from the
            // template running out of time, and both from the template being wrong.
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            // The budget's stop, in whatever form it takes: ScriptAbortException, OperationCanceledException, or one a built-in threw wrapped in
            // ScriptRuntimeException. Only the exception itself says so: a template that fails for its own reasons can take past the budget to unwind.
            catch (Exception ex) when (budget.IsCancellationRequested && IsStop(ex))
            {
                throw new RenderException($"The template took longer than {limit.TotalMilliseconds} ms.", retryable: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Whatever else a template makes Scriban or a built-in throw (a runtime error, a stack too deep, memory) is the template's.
                throw new RenderException("The template failed: " + ex.Message);
            }
            if (output.Length > MaxOutputChars)
            {
                throw new RenderException($"The template produced more than {MaxOutputChars / 1024} KB.");
            }
            return output;
        }

        private static bool IsStop(Exception? ex)
        {
            for (; ex is not null; ex = ex.InnerException)
            {
                if (ex is Scriban.Syntax.ScriptAbortException or OperationCanceledException)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The stack a render gets. Scriban evaluates recursively, and a template's own functions multiply the depth of their bodies by the call
        /// depth (up to <c>RecursiveLimit</c>): <see cref="MaxDepth"/> × 21 levels have to fit. A pool thread's 1–1.5 MB holds about 200, and an
        /// overflow ends the process, which no catch survives. Reserved, not committed: pages are only touched as deep as the render goes.
        /// </summary>
        private const int RenderStackBytes = 128 * 1024 * 1024; // the costliest measured pattern (pipes) needs about 56 MB on x64

        /// <summary>
        /// Renders synchronously on a thread of its own with <see cref="RenderStackBytes"/> of stack, in the invariant culture so the host's
        /// <c>LANG</c> can't change the output (number formatting, and anything else of Scriban's that reads the current culture).
        /// </summary>
        private static Task<string> RenderOnOwnStackAsync(Template template, TemplateContext context)
        {
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
                try
                {
                    result.SetResult(template.Render(context));
                }
                catch (Exception ex)
                {
                    result.SetException(ex);
                }
            }, RenderStackBytes)
            {
                IsBackground = true,
                Name = "template-render",
            };
            thread.Start();
            return result.Task;
        }

        /// <summary>The functions a template may use that Scriban would otherwise provide, and its two empty values.</summary>
        private static readonly string[] Functions = ["string", "array", "math", "html", "json"];
        private static readonly HashSet<string> Allowed = new([.. Functions, "empty", "blank"], StringComparer.Ordinal);

        /// <summary>What a template sees of the request (<see cref="Globals"/> fills them).</summary>
        private static readonly string[] ModelNames = ["data", "labels", "links", "type", "type_version", "type_name", "priority"];

        /// <summary>Scriban's own built-ins that the sandbox leaves out: <c>include</c>, <c>date</c>, <c>object</c>, <c>regex</c>, <c>timespan</c>…</summary>
        private static readonly HashSet<string> Withheld = new TemplateContext().BuiltinObject.Keys.Where(k => !Allowed.Contains(k)).ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// Checks a template at declaration (design §8.2) without rendering it: it parses, and nowhere reaches for what the sandbox withholds
        /// (<c>include</c>, <c>import</c>, <c>date</c>…) from the global scope, in any branch. Null when it is fine. Rendering isn't a check: data
        /// decides which branches run, and a template that does arithmetic on a field that empty data leaves null is fine with real data.
        /// <para>
        /// A name in another position is the template's own, not a built-in: a member (<c>data.date</c>), an object key (<c>{date: 1}</c>), a
        /// parameter, a loop variable or anything the template assigns, and any name inside a <c>with</c> block (a member of its object). The
        /// sandbox doesn't contain the withheld names anyway, so this check is for telling authors early, not the only guard.
        /// </para>
        /// </summary>
        public static string? Check(string body)
        {
            var template = Template.Parse(body);
            if (template.HasErrors)
            {
                return "The template doesn't parse: " + string.Join("; ", template.Messages.Select(m => m.ToString()));
            }
            // First, so the walks below (recursive) only ever see a shallow tree.
            if (TooDeep(template.Page))
            {
                return DepthMessage;
            }
            var own = new HashSet<string>(StringComparer.Ordinal);
            CollectOwnNames(template.Page, own);
            return FirstWithheld(template.Page, own);
        }

        public const int MaxTemplateChars = 64 * 1024;

        /// <summary>
        /// How deep a template's syntax tree may go. Scriban limits nesting of brackets, blocks and operators while parsing, but not member and
        /// indexer chains (<c>data.a.a.a…</c>, <c>x[0][0]…</c>), which it renders recursively: about 500 links overflow the stack, which no
        /// catch survives. Real templates stay far below this.
        /// </summary>
        private const int MaxDepth = 250;

        private const string DepthMessage = "The template nests too deeply: more than 250 levels (a long member or index chain, else-if ladder, case, pipe or concatenation). Split it with variables.";

        /// <summary>Measured without recursion, so the measuring can't overflow either.</summary>
        private static bool TooDeep(Scriban.Syntax.ScriptNode? root)
        {
            if (root is null)
            {
                return false;
            }
            var pending = new Stack<(Scriban.Syntax.ScriptNode Node, int Depth)>();
            pending.Push((root, 1));
            while (pending.TryPop(out var current))
            {
                if (current.Depth > MaxDepth)
                {
                    return true;
                }
                foreach (var child in current.Node.Children)
                {
                    if (child is not null)
                    {
                        pending.Push((child, current.Depth + 1));
                    }
                }
            }
            return false;
        }

        /// <summary>Members of an allowed object that the sandbox removes: they differ on every render.</summary>
        private static readonly Dictionary<string, string[]> WithheldMembers = new(StringComparer.Ordinal) { ["math"] = ["random", "uuid"] };

        /// <summary>Names the template defines: assignments, loop variables, its functions and their parameters.</summary>
        private static void CollectOwnNames(Scriban.Syntax.ScriptNode? node, HashSet<string> own)
        {
            switch (node)
            {
                case null:
                    return;
                case Scriban.Syntax.ScriptAssignExpression { Target: Scriban.Syntax.ScriptVariable { Name: { } assigned } }:
                    own.Add(assigned);
                    break;
                case Scriban.Syntax.ScriptCaptureStatement { Target: Scriban.Syntax.ScriptVariable { Name: { } captured } }:
                    own.Add(captured);
                    break;
                case Scriban.Syntax.ScriptForStatement { Variable: Scriban.Syntax.ScriptVariable { Name: { } loop } }:
                    own.Add(loop);
                    break;
                case Scriban.Syntax.ScriptFunction { NameOrDoToken: Scriban.Syntax.ScriptVariable { Name: { } function } }:
                    own.Add(function);
                    break;
                case Scriban.Syntax.ScriptParameter { Name: { } parameter }:
                    own.Add(parameter.Name);
                    break;
            }
            foreach (var child in node.Children)
            {
                CollectOwnNames(child, own);
            }
        }

        private static string? FirstWithheld(Scriban.Syntax.ScriptNode? node, HashSet<string> own)
        {
            switch (node)
            {
                case null:
                    return null;
                case Scriban.Syntax.ScriptImportStatement:
                    return "import isn't available in templates.";
                case Scriban.Syntax.ScriptVariableGlobal { Name: { } name } when Withheld.Contains(name) && !own.Contains(name):
                    return $"'{name}' isn't available in templates: they can use {string.Join(", ", ModelNames)} and the "
                        + $"{string.Join(", ", Functions)} functions.";
                case Scriban.Syntax.ScriptMemberExpression
                {
                    Target: Scriban.Syntax.ScriptVariableGlobal { Name: { } owner }, Member: Scriban.Syntax.ScriptVariable { Name: { } name },
                } when WithheldMembers.TryGetValue(owner, out var withheld) && withheld.Contains(name) && !own.Contains(owner):
                    return $"'{owner}.{name}' isn't available in templates: it differs on every render.";
                // Only the object is looked up globally; the member is a name on it.
                case Scriban.Syntax.ScriptMemberExpression member:
                    return FirstWithheld(member.Target, own);
                case Scriban.Syntax.ScriptObjectMember objectMember:
                    return FirstWithheld(objectMember.Value, own);
                // Inside, a bare name is a member of the object first.
                case Scriban.Syntax.ScriptWithStatement with:
                    return FirstWithheld(with.Name, own);
            }
            foreach (var child in node.Children)
            {
                if (FirstWithheld(child, own) is { } problem)
                {
                    return problem;
                }
            }
            return null;
        }

        private static ScriptObject Builtins()
        {
            var builtins = new ScriptObject();
            var strings = new StringFunctions();
            foreach (var (name, function) in OrdinalStrings.Functions)
            {
                strings.Remove(name);
                strings.SetValue(name, function, true);
            }
            builtins.SetValue("string", strings, true);
            var arrays = new ArrayFunctions();
            arrays.Remove("sort");
            arrays.SetValue("sort", OrdinalSort.Function, true);
            builtins.SetValue("array", arrays, true);
            var math = new MathFunctions();
            foreach (var name in WithheldMembers["math"])
            {
                math.Remove(name);
            }
            builtins.SetValue("math", math, true);
            builtins.SetValue("html", new HtmlFunctions(), true);
            builtins.SetValue("json", Json(), true);
            return builtins;
        }

        /// <summary>
        /// Scriban's <c>string.starts_with</c>, <c>ends_with</c>, <c>index_of</c> and <c>equals_ignore_case</c> compare by culture, which under
        /// ICU ignores characters such as a soft hyphen: a host with invariant globalization would render the same template differently
        /// (deferred #12). These compare ordinally, so rendering doesn't depend on ICU; <c>index_of</c>'s culture comparisons are taken as their
        /// ordinal counterparts. Built once: each render gets these same function objects (they hold no state).
        /// </summary>
        private static class OrdinalStrings
        {
            public static bool StartsWith(string? text, string? value) => text is not null && value is not null && text.StartsWith(value, StringComparison.Ordinal);

            public static bool EndsWith(string? text, string? value) => text is not null && value is not null && text.EndsWith(value, StringComparison.Ordinal);

            public static bool EqualsIgnoreCase(string? text, string? other) => string.Equals(text, other, StringComparison.OrdinalIgnoreCase);

            public static int IndexOf(string text, string search, int? startIndex = null, int? count = null, string? stringComparison = null)
                => StringFunctions.IndexOf(text, search, startIndex, count, Ordinal(stringComparison));

            /// <summary>
            /// Any name (padded, in any case) or number of a <see cref="StringComparison"/>, made ordinal; anything else is refused here, so no
            /// value reaches Scriban's culture comparison.
            /// </summary>
            private static string Ordinal(string? comparison)
            {
                if (comparison is null)
                {
                    return nameof(StringComparison.Ordinal);
                }
                if (!Enum.TryParse<StringComparison>(comparison, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    throw new ArgumentException($"'{comparison}' is not a valid stringComparison", "stringComparison");
                }
                return parsed is StringComparison.CurrentCultureIgnoreCase or StringComparison.InvariantCultureIgnoreCase or StringComparison.OrdinalIgnoreCase
                    ? nameof(StringComparison.OrdinalIgnoreCase) : nameof(StringComparison.Ordinal);
            }

            internal static readonly ScriptObject Functions = Build();

            private static ScriptObject Build()
            {
                var functions = new ScriptObject();
                functions.Import(typeof(OrdinalStrings));
                return functions;
            }
        }

        /// <summary>
        /// <c>array.sort</c> as Scriban's (a list, optionally by a member or a dotted member path; a stable sort; null first), but comparing
        /// text ordinally (deferred #12): Scriban's compares by culture, which under ICU ignores a soft hyphen and puts <c>a</c> before <c>B</c>,
        /// so a host without ICU would render differently. Numbers compare by value whatever their type (<c>1</c> and <c>1.5</c>), where Scriban's
        /// failed on an integer next to a fraction. Values of different kinds (text and a number) don't compare: the render fails, as Scriban's did.
        /// </summary>
        private static class OrdinalSort
        {
            internal static readonly IScriptCustomFunction Function = DynamicCustomFunction.Create(null, typeof(OrdinalSort).GetMethod(nameof(Sort))!);

            public static System.Collections.IEnumerable Sort(TemplateContext context, Scriban.Parsing.SourceSpan span, object? list, string? member = null)
            {
                if (list is null)
                {
                    return new ScriptRange();
                }
                if (list is not System.Collections.IEnumerable enumerable)
                {
                    return new ScriptArray(1) { list };
                }
                var items = new List<object?>();
                foreach (var item in enumerable)
                {
                    // As Scriban's internal iteration: the loop limit and the render's budget hold.
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (context.LoopLimit != 0 && items.Count >= context.LoopLimit)
                    {
                        throw new Scriban.Syntax.ScriptRuntimeException(span, $"Exceeding number of iteration limit `{context.LoopLimit}` for internal iteration.");
                    }
                    items.Add(item);
                }
                var values = string.IsNullOrEmpty(member) ? items : items.Select(item => SortValue(context, span, item, member)).ToList();
                // Checked before sorting, so the render says why: a comparer's exception reaches it as "Failed to compare two elements".
                var keys = Keys(span, values);
                return new ScriptArray(items.Select((item, i) => (Item: item, Key: keys[i])).OrderBy(p => p.Key, Comparer<object?>.Create(Compare))
                    .Select(p => p.Item).ToList());
            }

            /// <summary>
            /// What each value sorts by, once per value: text, or a number of one type as itself; numbers of several types as their exact
            /// <see cref="Fraction"/>; nulls stay null. Values of different kinds (text and a number), or ones that don't compare, fail the render.
            /// </summary>
            private static object?[] Keys(Scriban.Parsing.SourceSpan span, List<object?> values)
            {
                var present = values.Where(v => v is not null).ToList();
                if (present.Count < 2)
                {
                    return [.. values]; // nothing to compare but nulls, which go first whatever the value is
                }
                var kinds = present.Select(Kind).Distinct().ToList();
                if (kinds.Count > 1)
                {
                    throw new Scriban.Syntax.ScriptRuntimeException(span,
                        $"array.sort can't compare {kinds[0]} with {kinds[1]}: sort values of one kind (all text or all numbers).");
                }
                var types = present.Select(v => v!.GetType()).Distinct().Count();
                if (kinds[0] == "a number" && types > 1)
                {
                    return [.. values.Select(v => v is null ? null : (object)Fraction.Of(v))];
                }
                if (types > 1 || present[0] is not IComparable)
                {
                    throw new Scriban.Syntax.ScriptRuntimeException(span, $"array.sort can't compare {kinds[0]} values: sort text, numbers or booleans.");
                }
                return [.. values];
            }

            /// <summary>Null first; text ordinally; anything else (one type, checked by <see cref="Keys"/>) by its own comparison.</summary>
            private static int Compare(object? a, object? b) => (a, b) switch
            {
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                (string x, string y) => string.CompareOrdinal(x, y),
                _ => ((IComparable)a).CompareTo(b), // one numeric type, Fraction, bool, char: no culture involved
            };

            /// <summary>The member, else the dotted path (as Scriban looks it up); null when there is none.</summary>
            private static object? SortValue(TemplateContext context, Scriban.Parsing.SourceSpan span, object? target, string member)
            {
                if (TryMember(context, span, target, member, out var value))
                {
                    return value;
                }
                if (member.Contains('.'))
                {
                    value = target;
                    foreach (var part in member.Split('.'))
                    {
                        if (value is null || !TryMember(context, span, value, part, out value))
                        {
                            return null;
                        }
                    }
                    return value;
                }
                return null;
            }

            private static bool TryMember(TemplateContext context, Scriban.Parsing.SourceSpan span, object? target, string member, out object? value)
            {
                value = null;
                if (target is null)
                {
                    return false;
                }
                return context.GetMemberAccessor(target).TryGetValue(context, span, target, member, out value)
                    || (context.TryGetMember?.Invoke(context, span, target, member, out value) ?? false);
            }

            private static string Kind(object? value) => value switch
            {
                string => "text",
                bool => "a boolean",
                _ when IsNumber(value!) => "a number",
                _ => "an object",
            };

            private static bool IsNumber(object value) => value is sbyte or byte or short or ushort or int or uint or long or ulong
                or float or double or decimal or System.Numerics.BigInteger;

            /// <summary>
            /// A number by exact value, for numbers of different types: each as the fraction it is (a double is a binary fraction, a decimal a
            /// decimal one), so the order doesn't depend on which pairs are compared how, and is transitive (comparing some pairs as doubles and
            /// others as decimals isn't). NaN sorts first, then negative infinity; positive infinity last.
            /// </summary>
            private readonly record struct Fraction(System.Numerics.BigInteger Numerator, System.Numerics.BigInteger Denominator, int Rank) : IComparable
            {
                public int CompareTo(object? other)
                {
                    var that = (Fraction)other!;
                    if (Rank != 0 || that.Rank != 0)
                    {
                        return Rank.CompareTo(that.Rank);
                    }
                    return Denominator.IsOne && that.Denominator.IsOne ? Numerator.CompareTo(that.Numerator)
                        : (Numerator * that.Denominator).CompareTo(that.Numerator * Denominator);
                }

                public static Fraction Of(object value)
                {
                    var (numerator, denominator, rank) = Exact(value);
                    return new Fraction(numerator, denominator, rank);
                }
            }

            /// <summary>A finite number as numerator / denominator (denominator positive); <c>Rank</c> -2 for NaN, ∓1 for the infinities.</summary>
            private static (System.Numerics.BigInteger Numerator, System.Numerics.BigInteger Denominator, int Rank) Exact(object value)
            {
                switch (value)
                {
                    case System.Numerics.BigInteger big:
                        return (big, 1, 0);
                    case ulong u:
                        return (u, 1, 0);
                    case decimal d:
                    {
                        var bits = decimal.GetBits(d);
                        var mantissa = new System.Numerics.BigInteger((uint)bits[0])
                            | (new System.Numerics.BigInteger((uint)bits[1]) << 32) | (new System.Numerics.BigInteger((uint)bits[2]) << 64);
                        var scale = (bits[3] >> 16) & 0xFF;
                        return (bits[3] < 0 ? -mantissa : mantissa, System.Numerics.BigInteger.Pow(10, scale), 0);
                    }
                    case float or double:
                    {
                        var number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); // a float widens exactly
                        if (double.IsNaN(number))
                        {
                            return (0, 1, -2);
                        }
                        if (double.IsInfinity(number))
                        {
                            return (0, 1, number < 0 ? -1 : 1);
                        }
                        var bits = BitConverter.DoubleToInt64Bits(number);
                        var exponent = (int)((bits >> 52) & 0x7FF);
                        var fraction = bits & 0xFFFFFFFFFFFFFL;
                        var (mantissa, power) = exponent == 0 ? (fraction, -1074) : (fraction | (1L << 52), exponent - 1075);
                        System.Numerics.BigInteger numerator = number < 0 ? -mantissa : mantissa;
                        return power >= 0 ? (numerator << power, 1, 0) : (numerator, System.Numerics.BigInteger.One << -power, 0);
                    }
                    default: // the other integer types
                        return (Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture), 1, 0);
                }
            }
        }

        /// <summary>
        /// For webhook templates that build JSON: <c>json.escape</c> makes a value safe inside a JSON string (<c>"{{ data.text | json.escape }}"</c>),
        /// <c>json.encode</c> writes a value as JSON (<c>"data": {{ data | json.encode }}</c>). Without them, a data value could close a string and
        /// add fields of its own.
        /// </summary>
        private static ScriptObject Json()
        {
            var json = new ScriptObject();
            // As the template would print it (true, not True), then escaped for inside a JSON string.
            json.Import("escape", new Func<TemplateContext, object?, string>((context, value) => JsonEncodedText.Encode(value switch
            {
                null => "",
                string s => s,
                _ => context.ObjectToString(value) ?? "",
            }).ToString()));
            json.Import("encode", new Func<TemplateContext, object?, string>(Encode));
            return json;
        }

        private const int MaxJsonDepth = 64;

        /// <summary>
        /// A template value as JSON, held to the sandbox's limits as it is written: one built-in call runs to its end, so the limits that apply
        /// between steps don't reach inside it. It stops at the output limit (a value built from shared parts can be enormous written out), at 64
        /// levels, at a value that contains itself, and when the render is cancelled.
        /// </summary>
        private static string Encode(TemplateContext context, object? value)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = MaxJsonDepth + 1 }))
            {
                Write(context, writer, value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        private static void Write(TemplateContext context, Utf8JsonWriter writer, object? value, HashSet<object> path, int depth)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (writer.BytesCommitted + writer.BytesPending > MaxOutputChars)
            {
                throw new InvalidOperationException($"json.encode would produce more than {MaxOutputChars / 1024} KB.");
            }
            if (depth > MaxJsonDepth)
            {
                throw new InvalidOperationException($"json.encode goes deeper than {MaxJsonDepth} levels.");
            }
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    return;
                case string s:
                    writer.WriteStringValue(s);
                    return;
                case bool b:
                    writer.WriteBooleanValue(b);
                    return;
                case int i:
                    writer.WriteNumberValue(i);
                    return;
                case long l:
                    writer.WriteNumberValue(l);
                    return;
                case decimal m:
                    writer.WriteNumberValue(m);
                    return;
                case double d when double.IsFinite(d):
                    writer.WriteNumberValue(d);
                    return;
                case float f when float.IsFinite(f):
                    writer.WriteNumberValue(f);
                    return;
                case ScriptObject or System.Collections.IEnumerable when !path.Add(value):
                    throw new InvalidOperationException("json.encode: the value contains itself.");
                case ScriptObject o:
                    writer.WriteStartObject();
                    foreach (var (key, item) in o)
                    {
                        writer.WritePropertyName(key);
                        Write(context, writer, item, path, depth + 1);
                    }
                    writer.WriteEndObject();
                    path.Remove(value);
                    return;
                case System.Collections.IEnumerable items:
                    writer.WriteStartArray();
                    foreach (var item in items)
                    {
                        Write(context, writer, item, path, depth + 1);
                    }
                    writer.WriteEndArray();
                    path.Remove(value);
                    return;
                default:
                    writer.WriteStringValue(context.ObjectToString(value));
                    return;
            }
        }

        private static ScriptObject Globals(RenderModel model)
        {
            var globals = new ScriptObject
            {
                ["data"] = ToScript(model.Data),
                ["labels"] = Dictionary(model.Labels),
                ["links"] = Dictionary(model.Links),
                ["type"] = model.Type,
                ["type_version"] = model.TypeVersion,
                ["type_name"] = model.TypeName,
                ["priority"] = model.Priority.ToString().ToLowerInvariant(),
            };
            return globals;
        }

        private static ScriptObject Dictionary(IReadOnlyDictionary<string, string> values)
        {
            var o = new ScriptObject();
            foreach (var (key, value) in values)
            {
                o[key] = value;
            }
            return o;
        }

        private static object? ToScript(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().Aggregate(new ScriptObject(), (o, p) => { o[p.Name] = ToScript(p.Value); return o; }),
            JsonValueKind.Array => new ScriptArray(e.EnumerateArray().Select(ToScript)),
            JsonValueKind.String => e.GetString(),
            // A whole number past long, or a decimal fraction, stays exact (an id like 12345678901234567890 must come out the same).
            // Only when the decimal is the number as written: 1e-30 fits a decimal only as 0.
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l
                : e.TryGetDecimal(out var m) && m.ToString(System.Globalization.CultureInfo.InvariantCulture) == e.GetRawText() ? m
                : e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>What each channel's rendered content is (design §8.1), stored on the delivery and sent as is by every attempt.</summary>
    public static class ChannelContent
    {
        /// <summary>
        /// Email from a template's text: a first line <c>Subject: …</c> gives the subject (otherwise the type's name); the rest is the text body.
        /// The HTML body is that text, HTML-encoded, so no data value can inject markup.
        /// </summary>
        public static string Email(string rendered, string fallbackSubject)
        {
            var text = rendered.Replace("\r\n", "\n").TrimStart('\n');
            var subject = fallbackSubject;
            if (text.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
            {
                var end = text.IndexOf('\n');
                subject = (end < 0 ? text[8..] : text[8..end]).Trim();
                text = end < 0 ? "" : text[(end + 1)..].TrimStart('\n');
            }
            // One line: a line break in a subject would start a new header, including the Unicode ones some mail software honours.
            subject = string.Join(' ', subject.Split(['\r', '\n', '\u0085', '\u2028', '\u2029'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var html = "<!doctype html><html><body><div style=\"font-family:sans-serif;white-space:pre-wrap\">"
                + WebUtility.HtmlEncode(text.TrimEnd()) + "</div></body></html>";
            return JsonSerializer.Serialize(new EmailContent(subject.Length > 0 ? Cut(subject, 250) : fallbackSubject, text.TrimEnd(), html));
        }

        /// <summary>At most <paramref name="max"/> UTF-16 units, never splitting a surrogate pair (half an emoji isn't text).</summary>
        [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
        public static string? Cut(string? text, int max)
        {
            if (text is null || text.Length <= max)
            {
                return text;
            }
            var end = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
            return text[..end];
        }

        /// <summary>A webhook body: the passthrough envelope (design §8.1), or a template's output (JSON if it parses as JSON, else plain text).</summary>
        public static string Webhook(string body, bool isJson) => JsonSerializer.Serialize(new WebhookContent(isJson ? "application/json" : "text/plain; charset=utf-8", body));

        public static bool IsJson(string text)
        {
            try
            {
                using var _ = JsonDocument.Parse(text);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    public sealed record EmailContent(string Subject, string Text, string Html);

    public sealed record WebhookContent(string ContentType, string Body);
}
