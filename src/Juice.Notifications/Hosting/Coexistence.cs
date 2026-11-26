using System.Reflection;

namespace Juice.Notifications.Hosting
{
    /// <summary>
    /// What is the pipeline's own in a container it shares with a host (phase-5 waves §5.2, U2): its commands, by the assemblies that declare
    /// them (not by name), and its outbox messages, by their <see cref="Juice.Messaging.Attributes.DomainAttribute"/>. A host's commands get no
    /// notifications transaction (<see cref="Data.NotificationsPipelineTransaction{TRequest, TResponse}"/>), and its messages no notifications
    /// route.
    /// </summary>
    public static class NotificationsPipeline
    {
        /// <summary>The domain the pipeline's outbox messages carry (<c>[Domain(NotificationsPipeline.Domain)]</c>); the routes answer only for it.</summary>
        public const string Domain = "juice.notifications";

        /// <summary>The assemblies whose commands the pipeline's transaction wraps: the core's, where every command and its handler are.</summary>
        public static IReadOnlyCollection<Assembly> CommandAssemblies { get; } = [typeof(NotificationsPipeline).Assembly];
    }
}

namespace Juice.Notifications.Hosting
{
    /// <summary>
    /// Who is changing the setup in this flow, for a host's audit sink when the change's own record names no user (m44-architecture §6.4): an
    /// embedded host has no Juice HTTP user accessor, so the admin API names its signed-in caller here for the request.
    /// </summary>
    public static class NotificationsActor
    {
        private static readonly AsyncLocal<string?> Actor = new();

        public static string? Current => Actor.Value;

        /// <summary>Names <paramref name="actor"/> until the result is disposed (then the previous one again).</summary>
        public static IDisposable As(string? actor)
        {
            var previous = Actor.Value;
            Actor.Value = actor;
            return new Restore(previous);
        }

        private sealed class Restore(string? previous) : IDisposable
        {
            public void Dispose() => Actor.Value = previous;
        }
    }
}
