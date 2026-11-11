using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Writes the host's errors to a file next to the tests (errors.log), so a 500 in a test can be explained.</summary>
    public sealed class ErrorLogProvider : ILoggerProvider
    {
        private static readonly object Gate = new();
        public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "errors.log");

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }
                lock (Gate)
                {
                    File.AppendAllText(Path, $"{DateTimeOffset.UtcNow:O} {logLevel} {category}: {formatter(state, exception)}\n{exception}\n\n");
                }
            }
        }
    }
}
