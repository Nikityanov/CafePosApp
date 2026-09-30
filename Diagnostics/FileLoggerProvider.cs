using Microsoft.Extensions.Logging;

namespace CafePosApp.Diagnostics;

/// <summary>Routes Microsoft.Extensions.Logging messages into the rotating local log file.</summary>
public sealed class FileLoggerProvider(LogFileWriter writer, LogLevel minimumLevel = LogLevel.Information) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(writer, categoryName, minimumLevel);

    public void Dispose()
    {
        // The writer itself is a singleton and stays alive for the whole app lifetime.
    }

    private sealed class FileLogger(LogFileWriter writer, string categoryName, LogLevel minimumLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var shortCategory = categoryName.Contains('.') ? categoryName[(categoryName.LastIndexOf('.') + 1)..] : categoryName;
            var message = $"{shortCategory}: {formatter(state, exception)}";
            if (exception is not null) message += $" | {exception.GetType().Name}: {exception.Message}";
            if (logLevel >= LogLevel.Warning && exception is not null) message += $" | {exception.StackTrace}";

            writer.Write(DateTimeOffset.Now, logLevel.ToString(), message);
        }
    }
}
