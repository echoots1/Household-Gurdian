namespace Guardian.Service.Infrastructure;

/// <summary>Tiny rolling file logger: one line per entry, 2 MB then .1 backup. No third-party logging stack.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _lock = new();
    public FileLoggerProvider(string path) { _path = path; Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    internal void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > 2_000_000) File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch { }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p; private readonly string _cat;
        public FileLogger(FileLoggerProvider p, string cat) { _p = p; _cat = cat; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information &&
            (!_cat.StartsWith("Microsoft.AspNetCore") || _cat.StartsWith("Microsoft.AspNetCore.Antiforgery") || _cat.StartsWith("Microsoft.AspNetCore.DataProtection") || logLevel >= LogLevel.Warning);
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            _p.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {_cat.Split('.')[^1]}: {fmt(state, ex)}{(ex is null ? "" : " | " + ex.GetType().Name + ": " + ex.Message)}");
        }
    }
}
