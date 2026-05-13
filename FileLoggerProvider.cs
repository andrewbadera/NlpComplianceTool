using Microsoft.Extensions.Logging;

namespace ComplianceCheck
{
    internal sealed class FileLoggerProvider : ILoggerProvider
    {
        private readonly string _filePath;
        private readonly object _sync = new();

        public FileLoggerProvider(string filePath)
        {
            _filePath = filePath;
        }

        public ILogger CreateLogger(string categoryName)
            => new FileLogger(categoryName, _filePath, _sync);

        public void Dispose() { }

        private sealed class FileLogger : ILogger
        {
            private readonly string _categoryName;
            private readonly string _filePath;
            private readonly object _sync;

            public FileLogger(string categoryName, string filePath, object sync)
            {
                _categoryName = categoryName;
                _filePath = filePath;
                _sync = sync;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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

                var message = formatter(state, exception);
                if (string.IsNullOrEmpty(message) && exception is null)
                {
                    return;
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_categoryName}: {message}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                lock (_sync)
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
            }
        }
    }
}
