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
        /// <c>LANG</c> can't change the output (<c>array.sort</c> compares by culture).
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
            builtins.SetValue("string", new StringFunctions(), true);
            builtins.SetValue("array", new ArrayFunctions(), true);
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
