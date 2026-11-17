using Microsoft.Extensions.Logging;

namespace PellyCode.McpServer;

/// <summary>
/// Appends log lines to a file (--log-file). Never touches stdout. Each line is written at the file's current end, so several
/// handles on one file (the startup-failure logger and the host's, or several servers sharing a log) never overwrite each other.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileLoggerProvider(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _writer = new StreamWriter(new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() => _writer.Dispose();

    private void Write(string line)
    {
        lock (_gate)
        {
            // FileMode.Append positions once at open: another handle may have written past that point since.
            _writer.BaseStream.Seek(0, SeekOrigin.End);
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            owner.Write(line);
        }
    }
}
