using Microsoft.Extensions.Logging;
using InstantFileShare.Infrastructure;

namespace InstantFileShare.Agent;

internal sealed class AgentFileLoggerProvider(InstantFileShare.Infrastructure.FileLogStore logStore) : ILoggerProvider
{
    private readonly InstantFileShare.Infrastructure.FileLogStore _logStore = logStore;

    public ILogger CreateLogger(string categoryName) => new AgentFileLogger(categoryName, _logStore);

    public void Dispose()
    {
    }

    private sealed class AgentFileLogger(string categoryName, InstantFileShare.Infrastructure.FileLogStore logStore) : ILogger
    {
        private readonly string _categoryName = categoryName;
        private readonly InstantFileShare.Infrastructure.FileLogStore _logStore = logStore;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = $"{logLevel} [{_categoryName}] {formatter(state, exception)}";
            if (exception is not null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }

            _ = _logStore.AppendAgentAsync(message, CancellationToken.None);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
