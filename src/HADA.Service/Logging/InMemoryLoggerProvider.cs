namespace HADA.Service.Logging;

/// <summary>Copies log messages into a <see cref="LogBuffer"/>. Filtered by the usual <c>Logging</c> configuration.</summary>
[ProviderAlias("Memory")]
public sealed class InMemoryLoggerProvider(LogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferLogger(buffer, categoryName);

    public void Dispose()
    {
    }

    private sealed class BufferLogger(LogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                buffer.Add(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
