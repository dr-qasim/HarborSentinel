namespace HarborSentinel.AdminHost.Services;

public sealed class RecentDiagnosticEventLoggerProvider : ILoggerProvider
{
    private readonly IRecentDiagnosticEventBuffer _buffer;

    public RecentDiagnosticEventLoggerProvider(IRecentDiagnosticEventBuffer buffer)
    {
        _buffer = buffer;
    }

    public ILogger CreateLogger(string categoryName)
        => new RecentDiagnosticEventLogger(categoryName, _buffer);

    public void Dispose()
    {
    }

    private sealed class RecentDiagnosticEventLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly IRecentDiagnosticEventBuffer _buffer;

        public RecentDiagnosticEventLogger(string categoryName, IRecentDiagnosticEventBuffer buffer)
        {
            _categoryName = categoryName;
            _buffer = buffer;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (exception is not null)
                message = $"{message} {exception.GetType().Name}: {exception.Message}".Trim();

            _buffer.Record(DateTimeOffset.UtcNow, logLevel, _categoryName, message);
        }
    }
}
