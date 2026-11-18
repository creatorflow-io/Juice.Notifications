using System.Text.Json;
using Juice.Notifications.Planning;

namespace Juice.Notifications.Workflows
{
    // A type's workflow (design §7.1, phase-3-plan I27–I30): steps run in order per notification. Declared as JSON, checked when the type is
    // declared, stored canonically on the type version, and parsed again (cached) when a notification runs it.

    public abstract record WorkflowStep(string Name);

    /// <summary>Marks the notification's waiting recipients (of the current escalation step) due for the delivery planner.</summary>
    public sealed record SendStep() : WorkflowStep(WorkflowSteps.Send);

    /// <summary>Waits a fixed time.</summary>
    public sealed record DelayStep(TimeSpan Delay) : WorkflowStep(WorkflowSteps.Delay);

    /// <summary>Goes on only if the matcher holds; otherwise the notification ends <c>ConditionNotMet</c>.</summary>
    public sealed record ConditionStep(Condition When) : WorkflowStep(WorkflowSteps.Condition);

    /// <summary>At most <see cref="Limit"/> notifications of the type per window per key; the rest wait for the next window.</summary>
    public sealed record ThrottleStep(int Limit, TimeSpan Window, string By) : WorkflowStep(WorkflowSteps.Throttle);

    /// <summary>
    /// Holds the waiting recipients in the open digest for their key, for <see cref="Window"/>; the digest sends them when it closes, one delivery
    /// per destination with an item each (D19). It stands for the <c>send</c> that must follow it.
    /// </summary>
    public sealed record DigestStep(TimeSpan Window, string By) : WorkflowStep(WorkflowSteps.Digest);

    /// <summary>
    /// Runs an escalation policy on the notification's correlation (design §7.1, phase-3-plan I34): the tenant's policy <see cref="Policy"/>, or
    /// the one a label names (<see cref="PolicyFromLabel"/>). The workflow goes on; the escalation runs on its own until the correlation closes.
    /// </summary>
    public sealed record EscalateStep(string? Policy, string? PolicyFromLabel) : WorkflowStep(WorkflowSteps.Escalate);

    /// <summary>What a digest groups by: a route recipient by its route, a person by themselves, the correlation, or the type.</summary>
    public static class DigestKeys
    {
        public const string Route = "route";
        public const string Subscriber = "subscriber";
        public const string Correlation = "correlation";
        public const string Type = "type";

        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Route, Subscriber, Correlation, Type };
    }

    public static class WorkflowSteps
    {
        public const string Send = "send";
        public const string Delay = "delay";
        public const string Condition = "condition";
        public const string Throttle = "throttle";
        public const string Digest = "digest";
        public const string Escalate = "escalate";
    }

    /// <summary>What a throttle counts by: the type, the correlation, or a label's value.</summary>
    public static class ThrottleKeys
    {
        public const string Type = "type";
        public const string Correlation = "correlation";
        public const string LabelPrefix = "labels.";
    }

    public sealed class Workflow
    {
        public const int MaxSteps = 20;
        public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(7);
        public const int MaxThrottle = 100_000;
        public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(1);

        /// <summary>The workflow of every phase 1 and 2 type, and of a type that declares none.</summary>
        public const string SendOnlyJson = """[{"step":"send"}]""";

        public IReadOnlyList<WorkflowStep> Steps { get; }

        private Workflow(IReadOnlyList<WorkflowStep> steps) => Steps = steps;

        public static Workflow SendOnly { get; } = new([new SendStep()]);

        /// <summary>Whether it is just <c>send</c>: the planner sends at once, with no workflow step between (I27's fast path).</summary>
        public bool IsSendOnly => Steps is [SendStep];

        /// <summary>
        /// Parses and checks a declared workflow (pointers under <c>/workflow</c>). <paramref name="correlation"/> is the type's correlation rule:
        /// a step that reads the correlation needs a type whose requests can carry one.
        /// </summary>
        public static (Workflow? Workflow, IReadOnlyList<FieldError> Errors) Parse(JsonElement json, string correlation)
        {
            var errors = new List<FieldError>();
            if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() is 0 or > MaxSteps)
            {
                return (null, [new FieldError("/workflow", $"A workflow is a list of 1–{MaxSteps} steps: send, delay, condition, throttle, digest, escalate.")]);
            }
            var steps = new List<WorkflowStep>();
            var index = 0;
            foreach (var element in json.EnumerateArray())
            {
                var at = $"/workflow/{index++}";
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("step", out var name) || name.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new FieldError(at, "A step is an object naming its step: { \"step\": \"send\" }."));
                    continue;
                }
                var step = name.GetString()!;
                var allowed = step switch
                {
                    WorkflowSteps.Send => new[] { "step" },
                    WorkflowSteps.Delay => ["step", "seconds"],
                    WorkflowSteps.Condition => ["step", "when"],
                    WorkflowSteps.Throttle => ["step", "limit", "windowSeconds", "by"],
                    WorkflowSteps.Digest => ["step", "windowSeconds", "by"],
                    WorkflowSteps.Escalate => ["step", "policy", "policyFrom"],
                    _ => null,
                };
                if (allowed is null)
                {
                    errors.Add(new FieldError(at + "/step", "A step is send, delay, condition, throttle, digest or escalate."));
                    continue;
                }
                foreach (var property in element.EnumerateObject().Where(p => !allowed.Contains(p.Name)))
                {
                    errors.Add(new FieldError($"{at}/{Types.TypeRules.Pointer(property.Name)}", $"A {step} step has {string.Join(", ", allowed.Skip(1).DefaultIfEmpty("nothing else"))}."));
                }
                switch (step)
                {
                    case WorkflowSteps.Send:
                        steps.Add(new SendStep());
                        break;
                    case WorkflowSteps.Delay:
                        if (Seconds(element, "seconds", 1, (int)MaxDelay.TotalSeconds) is { } delay)
                        {
                            steps.Add(new DelayStep(TimeSpan.FromSeconds(delay)));
                        }
                        else
                        {
                            errors.Add(new FieldError(at + "/seconds", $"A delay is 1–{(int)MaxDelay.TotalSeconds} seconds (7 days)."));
                        }
                        break;
                    case WorkflowSteps.Condition:
                        if (!element.TryGetProperty("when", out var when))
                        {
                            errors.Add(new FieldError(at + "/when", "A condition says when to go on: { \"when\": { \"labels\": { … } } }."));
                            break;
                        }
                        var (condition, problems) = Condition.Parse(when, at + "/when", 1);
                        errors.AddRange(problems);
                        if (condition is not null && condition.ReadsCorrelation && correlation == CorrelationRules.None)
                        {
                            errors.Add(new FieldError(at + "/when", "This type's requests carry no correlation key, so a condition can't read its state."));
                        }
                        else if (condition is not null)
                        {
                            steps.Add(new ConditionStep(condition));
                        }
                        break;
                    case WorkflowSteps.Throttle:
                        var limit = Seconds(element, "limit", 1, MaxThrottle);
                        var window = Seconds(element, "windowSeconds", 1, (int)MaxWindow.TotalSeconds);
                        var by = element.TryGetProperty("by", out var byElement) && byElement.ValueKind == JsonValueKind.String ? byElement.GetString()! : ThrottleKeys.Type;
                        if (limit is null)
                        {
                            errors.Add(new FieldError(at + "/limit", $"A throttle's limit is 1–{MaxThrottle} notifications per window."));
                        }
                        if (window is null)
                        {
                            errors.Add(new FieldError(at + "/windowSeconds", $"A throttle's window is 1–{(int)MaxWindow.TotalSeconds} seconds (a day)."));
                        }
                        if (!IsThrottleKey(by))
                        {
                            errors.Add(new FieldError(at + "/by", "A throttle counts by type, correlation or labels.<name>."));
                        }
                        else if (by == ThrottleKeys.Correlation && correlation != CorrelationRules.Required)
                        {
                            errors.Add(new FieldError(at + "/by", "Counting by correlation needs a type whose requests always carry a key (correlation: required)."));
                        }
                        if (limit is { } l && window is { } w && IsThrottleKey(by))
                        {
                            steps.Add(new ThrottleStep(l, TimeSpan.FromSeconds(w), by));
                        }
                        break;
                    case WorkflowSteps.Digest:
                        var digestWindow = Seconds(element, "windowSeconds", 1, (int)MaxWindow.TotalSeconds);
                        var digestBy = element.TryGetProperty("by", out var digestByElement) && digestByElement.ValueKind == JsonValueKind.String
                            ? digestByElement.GetString()! : DigestKeys.Route;
                        if (digestWindow is null)
                        {
                            errors.Add(new FieldError(at + "/windowSeconds", $"A digest's window is 1–{(int)MaxWindow.TotalSeconds} seconds (a day)."));
                        }
                        if (!DigestKeys.All.Contains(digestBy))
                        {
                            errors.Add(new FieldError(at + "/by", "A digest groups by route, subscriber, correlation or type."));
                        }
                        else if (digestBy == DigestKeys.Correlation && correlation != CorrelationRules.Required)
                        {
                            errors.Add(new FieldError(at + "/by", "Grouping by correlation needs a type whose requests always carry a key (correlation: required)."));
                        }
                        // It sends what it holds: the send after it is its own (phase-3-plan I31).
                        var next = json.GetArrayLength() > index ? json[index] : default;
                        if (next.ValueKind != JsonValueKind.Object || !next.TryGetProperty("step", out var nextStep) || nextStep.ValueKind != JsonValueKind.String
                            || nextStep.GetString() != WorkflowSteps.Send)
                        {
                            errors.Add(new FieldError(at, "A digest is followed by the send it holds its recipients for: { \"step\": \"send\" }."));
                        }
                        if (digestWindow is { } dw && DigestKeys.All.Contains(digestBy))
                        {
                            steps.Add(new DigestStep(TimeSpan.FromSeconds(dw), digestBy));
                        }
                        break;
                    case WorkflowSteps.Escalate:
                        var policy = element.TryGetProperty("policy", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                        var from = element.TryGetProperty("policyFrom", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                        string? label = null;
                        if ((policy is null) == (from is null))
                        {
                            errors.Add(new FieldError(at, "An escalation names its policy (\"policy\": \"key\") or the label that names it (\"policyFrom\": \"labels.name\"), not both."));
                        }
                        else if (policy is not null && !Configuration.ConfigurationRules.Key().IsMatch(policy))
                        {
                            errors.Add(new FieldError(at + "/policy", "A policy's key is 1–63 lowercase letters, digits or hyphens, starting with a letter."));
                        }
                        else if (from is not null && (!from.StartsWith(ThrottleKeys.LabelPrefix, StringComparison.Ordinal)
                            || !Intake.RequestRules.IsLabelName(label = from[ThrottleKeys.LabelPrefix.Length..])))
                        {
                            errors.Add(new FieldError(at + "/policyFrom", "Name the label that holds the policy's key: labels.<name>."));
                        }
                        else if (correlation == CorrelationRules.None)
                        {
                            errors.Add(new FieldError(at, "An escalation runs while its correlation is open: the type's requests must be able to carry a key."));
                        }
                        else if (steps.Any(s => s is EscalateStep))
                        {
                            errors.Add(new FieldError(at, "A workflow escalates once."));
                        }
                        else
                        {
                            steps.Add(new EscalateStep(policy, label));
                        }
                        break;
                }
            }
            if (errors.Count == 0 && !steps.Any(s => s is SendStep or DigestStep))
            {
                errors.Add(new FieldError("/workflow", "A workflow sends: give it a send step."));
            }
            return errors.Count > 0 ? (null, errors) : (new Workflow(steps), []);
        }

        /// <summary>A stored workflow (checked when declared). A row that no longer parses is a fault of the step, retried and given up.</summary>
        public static Workflow Read(string json)
        {
            if (json == SendOnlyJson)
            {
                return SendOnly;
            }
            using var document = JsonDocument.Parse(json);
            // The correlation rule was checked at declaration; reading needn't repeat it. Required is the rule every step passes (a throttle or
            // digest by correlation needs it), so a workflow its type accepted always reads back.
            var (workflow, errors) = Parse(document.RootElement, CorrelationRules.Required);
            return workflow ?? throw new InvalidOperationException($"A stored workflow doesn't parse: {string.Join("; ", errors.Select(e => e.Pointer + ": " + e.Message))}");
        }

        private static bool IsThrottleKey(string by)
            => by is ThrottleKeys.Type or ThrottleKeys.Correlation
                || by.StartsWith(ThrottleKeys.LabelPrefix, StringComparison.Ordinal) && Intake.RequestRules.IsLabelName(by[ThrottleKeys.LabelPrefix.Length..]);

        private static int? Seconds(JsonElement step, string name, int min, int max)
            => step.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= min && n <= max ? n : null;
    }

    /// <summary>
    /// A condition (design §7.2, D18): the correlation's state is one of a list; a label matcher (the route grammar); or all or any of those,
    /// nested at most 3 deep. It reads labels and correlation state only, never <c>data</c>. A missing label doesn't match (except <c>!=</c>), and
    /// a request without a correlation matches no correlation state.
    /// </summary>
    public abstract class Condition
    {
        public const int MaxDepth = 3;
        public const int MaxConditions = 10;

        public abstract bool Holds(IReadOnlyDictionary<string, string> labels, string? correlationState);

        public abstract bool ReadsCorrelation { get; }

        public static (Condition? Condition, IReadOnlyList<FieldError> Errors) Parse(JsonElement json, string at, int depth)
        {
            if (json.ValueKind != JsonValueKind.Object || json.EnumerateObject().Count() != 1)
            {
                return (null, [new FieldError(at, "A condition is one of { \"correlation\": [states] }, { \"labels\": { … } }, { \"all\": [ … ] } or { \"any\": [ … ] }.")]);
            }
            var property = json.EnumerateObject().Single();
            var inner = $"{at}/{property.Name}";
            switch (property.Name)
            {
                case "correlation":
                    var states = property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() is > 0 and <= 4
                        && property.Value.EnumerateArray().All(s => s.ValueKind == JsonValueKind.String && CorrelationStates.All.Contains(s.GetString()!))
                        ? property.Value.EnumerateArray().Select(s => s.GetString()!).ToHashSet(StringComparer.Ordinal)
                        : null;
                    return states is null
                        ? (null, [new FieldError(inner, "The correlation states are a list of open, acknowledged, resolved, cancelled.")])
                        : (new CorrelationIn(states), []);
                case "labels":
                    using (var match = JsonDocument.Parse("{\"labels\":" + property.Value.GetRawText() + "}"))
                    {
                        var (parsed, errors) = RouteMatch.Parse(match.RootElement);
                        return parsed is null
                            ? (null, errors.Select(e => new FieldError(e.Pointer.StartsWith("/match/labels", StringComparison.Ordinal) ? inner + e.Pointer["/match/labels".Length..] : inner, e.Message)).ToList())
                            : (new LabelsMatch(parsed), []);
                    }
                case "all" or "any":
                    if (depth >= MaxDepth)
                    {
                        return (null, [new FieldError(inner, $"Conditions nest at most {MaxDepth} deep.")]);
                    }
                    if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() is 0 or > MaxConditions)
                    {
                        return (null, [new FieldError(inner, $"{property.Name} lists 1–{MaxConditions} conditions.")]);
                    }
                    var parts = new List<Condition>();
                    var problems = new List<FieldError>();
                    var i = 0;
                    foreach (var part in property.Value.EnumerateArray())
                    {
                        var (condition, errors) = Parse(part, $"{inner}/{i++}", depth + 1);
                        problems.AddRange(errors);
                        if (condition is not null)
                        {
                            parts.Add(condition);
                        }
                    }
                    return problems.Count > 0 ? (null, problems) : (new Combined(property.Name == "all", parts), []);
                default:
                    return (null, [new FieldError(inner, "A condition is correlation, labels, all or any.")]);
            }
        }

        private sealed class CorrelationIn(HashSet<string> states) : Condition
        {
            public override bool Holds(IReadOnlyDictionary<string, string> labels, string? correlationState)
                => correlationState is not null && states.Contains(correlationState);

            public override bool ReadsCorrelation => true;
        }

        private sealed class LabelsMatch(RouteMatch match) : Condition
        {
            // The route grammar with labels only: any type, any priority.
            public override bool Holds(IReadOnlyDictionary<string, string> labels, string? correlationState)
                => match.Matches("", labels, NotificationPriority.Low);

            public override bool ReadsCorrelation => false;
        }

        private sealed class Combined(bool all, IReadOnlyList<Condition> parts) : Condition
        {
            public override bool Holds(IReadOnlyDictionary<string, string> labels, string? correlationState)
                => all ? parts.All(p => p.Holds(labels, correlationState)) : parts.Any(p => p.Holds(labels, correlationState));

            public override bool ReadsCorrelation => parts.Any(p => p.ReadsCorrelation);
        }
    }

    /// <summary>A correlation's states as conditions name them (design §5.3).</summary>
    public static class CorrelationStates
    {
        public const string Open = "open";
        public const string Acknowledged = "acknowledged";
        public const string Resolved = "resolved";
        public const string Cancelled = "cancelled";

        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Open, Acknowledged, Resolved, Cancelled };
    }
}
