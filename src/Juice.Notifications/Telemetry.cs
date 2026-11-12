using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Juice.Notifications
{
    /// <summary>
    /// The service's meter and activity source (design §14), both named <see cref="Name"/>. The core only emits; a host chooses exporters (the App
    /// sends them over OTLP when configured). Tags are low-cardinality names: type keys, channels, outcomes, categories, stage names.
    /// </summary>
    public static class Telemetry
    {
        public const string Name = "Juice.Notifications";

        public static readonly ActivitySource Source = new(Name);
        private static readonly Meter Meter = new(Name);

        public static readonly Counter<long> Requests = Meter.CreateCounter<long>("notifications.requests", description: "Requests by type and outcome.");
        public static readonly Counter<long> Recipients = Meter.CreateCounter<long>("notifications.recipients", description: "Recipients by outcome.");
        public static readonly Counter<long> Deliveries = Meter.CreateCounter<long>("notifications.deliveries", description: "Deliveries finished, by channel and status.");
        public static readonly Counter<long> Attempts = Meter.CreateCounter<long>("notifications.attempts", description: "Attempts by outcome and category.");
        public static readonly Counter<long> UnknownOutcomes = Meter.CreateCounter<long>("notifications.unknown_outcomes", description: "Sends with no answer, by channel.");
        public static readonly Counter<long> LeaseLost = Meter.CreateCounter<long>("notifications.lease_lost", description: "Steps whose claim had passed to another worker, by stage.");

        public static readonly Histogram<double> DispatchDelay = Meter.CreateHistogram<double>("notifications.dispatch_delay", unit: "s",
            description: "From a request's acceptance to its delivery's first attempt (target p95 ≤ 30 s).");

        public static readonly Histogram<double> ProviderLatency = Meter.CreateHistogram<double>("notifications.provider_latency", unit: "ms",
            description: "One provider call, by channel and outcome.");

        private static readonly List<Func<IEnumerable<Measurement<long>>>> BacklogSources = [];
        private static int _backlogRegistered;

        /// <summary>
        /// The gauge reports the most recently registered source still running (a process normally has one host; tests start several). Dispose the
        /// result when the source stops, so a stopped host's last count isn't reported for ever.
        /// </summary>
        public static IDisposable RegisterBacklog(Func<IEnumerable<Measurement<long>>> source)
        {
            lock (BacklogSources)
            {
                BacklogSources.Add(source);
            }
            if (Interlocked.Exchange(ref _backlogRegistered, 1) == 0)
            {
                Meter.CreateObservableGauge("notifications.due_backlog", Backlog, description: "Rows due now and not leased, by stage.");
            }
            return new Registration(() =>
            {
                lock (BacklogSources)
                {
                    BacklogSources.Remove(source);
                }
            });
        }

        private static IEnumerable<Measurement<long>> Backlog()
        {
            Func<IEnumerable<Measurement<long>>>? current;
            lock (BacklogSources)
            {
                current = BacklogSources.Count > 0 ? BacklogSources[^1] : null;
            }
            return current?.Invoke() ?? [];
        }

        private sealed class Registration(Action dispose) : IDisposable
        {
            private Action? _dispose = dispose;

            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }

        /// <summary>
        /// An activity for one stage step on a request, continuing the producer's trace (its <c>traceparent</c>, kept on the request) so a trace runs
        /// from the producer through every stage to the provider call. A linked new trace when the request carried none.
        /// </summary>
        public static Activity? StartStep(string name, string? traceParent, ActivityKind kind = ActivityKind.Internal)
            => ActivityContext.TryParse(traceParent, null, out var parent)
                ? Source.StartActivity(name, kind, parent)
                : Source.StartActivity(name, kind);
    }
}
