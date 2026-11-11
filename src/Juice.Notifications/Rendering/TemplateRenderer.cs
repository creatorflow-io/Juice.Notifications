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

    /// <summary>A render that failed: the delivery fails as <c>Content</c> (<c>render_failed</c>), without failover.</summary>
    public sealed class RenderException(string message) : Exception(message);

    /// <summary>
    /// The one Scriban sandbox every template runs in (design §8.2, D18, D22):
    /// <list type="bullet">
    /// <item>built-ins on an explicit allowlist only (<c>string</c>, <c>array</c>, <c>math</c>, <c>html</c>): no <c>include</c> or <c>import</c>,
    /// no template loader, nothing that reads the host clock or the environment;</item>
    /// <item>limits: 1,000 loop iterations, recursion 20, template 64 KB, output 256 KB, and 200 ms of rendering;</item>
    /// <item>a missing field renders empty, never an error.</item>
    /// </list>
    /// </summary>
    public static class TemplateRenderer
    {
        /// <summary>Recorded on each delivery with the content it produced (design D10).</summary>
        public const string Version = "scriban-7.2.5/1";
        public const int MaxOutputChars = 256 * 1024;
        public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);

        /// <summary>Renders a template; throws <see cref="RenderException"/> for anything the sandbox refuses.</summary>
        public static async Task<string> RenderAsync(string body, RenderModel model, CancellationToken ct)
        {
            var template = Template.Parse(body);
            if (template.HasErrors)
            {
                throw new RenderException("The template doesn't parse: " + string.Join("; ", template.Messages.Select(m => m.ToString())));
            }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(Budget);
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
            string output;
            try
            {
                output = await template.RenderAsync(context);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RenderException($"The template took longer than {Budget.TotalMilliseconds} ms.");
            }
            catch (Exception ex) when (ex is Scriban.Syntax.ScriptRuntimeException or InvalidOperationException or ArgumentException)
            {
                throw new RenderException("The template failed: " + ex.Message);
            }
            if (output.Length > MaxOutputChars)
            {
                throw new RenderException($"The template produced more than {MaxOutputChars / 1024} KB.");
            }
            return output;
        }

        /// <summary>
        /// Checks a template at declaration (design §8.2): it parses, and a dry run against an empty model uses nothing outside the sandbox. A
        /// branch the empty model doesn't reach is checked when it first renders.
        /// </summary>
        public static async Task<string?> CheckAsync(string body, CancellationToken ct)
        {
            try
            {
                await RenderAsync(body, new RenderModel("t", 1, "t", NotificationPriority.Normal, JsonElement.Parse("{}"), new Dictionary<string, string>(),
                    new Dictionary<string, string>()), ct);
                return null;
            }
            catch (RenderException ex)
            {
                return ex.Message;
            }
        }

        private static ScriptObject Builtins()
        {
            var builtins = new ScriptObject();
            builtins.SetValue("string", new StringFunctions(), true);
            builtins.SetValue("array", new ArrayFunctions(), true);
            builtins.SetValue("math", new MathFunctions(), true);
            builtins.SetValue("html", new HtmlFunctions(), true);
            return builtins;
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
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
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
            // One line: a line break in a subject would start a new header.
            subject = string.Join(' ', subject.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var html = "<!doctype html><html><body><div style=\"font-family:sans-serif;white-space:pre-wrap\">"
                + WebUtility.HtmlEncode(text.TrimEnd()) + "</div></body></html>";
            return JsonSerializer.Serialize(new EmailContent(subject.Length > 0 ? subject[..Math.Min(subject.Length, 250)] : fallbackSubject, text.TrimEnd(), html));
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
