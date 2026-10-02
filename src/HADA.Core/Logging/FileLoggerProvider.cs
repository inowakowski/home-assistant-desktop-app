using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Logging;

public sealed class FileLoggerOptions
{
    /// <summary>The current log file, e.g. <c>C:\ProgramData\HADA\logs\service.log</c>. Its folder is created when needed.</summary>
    public required string FilePath { get; init; }

    /// <summary>When the file reaches this size it becomes <c>name.1.log</c> and a new one is started.</summary>
    public long MaxFileBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>How many rolled files (<c>name.1.log</c>, <c>name.2.log</c>, …) are kept besides the current one.</summary>
    public int RetainedFiles { get; init; } = 3;

    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;
}

/// <summary>
/// Writes log entries to a size-limited file, so there is something to look at after a problem that nobody was
/// watching. Entries are queued and written by a background task; logging never blocks and never throws, and when
/// the file cannot be written (disk full, no permission) entries are dropped.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan FlushOnDisposeTimeout = TimeSpan.FromSeconds(2);

    private readonly FileLoggerOptions _options;
    private readonly Channel<string> _entries = Channel.CreateBounded<string>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly Task _writer;

    public FileLoggerProvider(FileLoggerOptions options)
    {
        _options = options;
        _writer = Task.Run(WriteEntriesAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Writes what is still queued, within a short time limit, and stops.</summary>
    public void Dispose()
    {
        _entries.Writer.TryComplete();
        _writer.Wait(FlushOnDisposeTimeout);
    }

    private async Task WriteEntriesAsync()
    {
        while (await _entries.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_options.FilePath)!);
                RollIfTooLarge();

                // Shared, so the file can be read, and rolled by another process, while this one is running.
                using var stream = new FileStream(
                    _options.FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, Utf8NoBom);
                while (_entries.Reader.TryRead(out var entry))
                {
                    writer.WriteLine(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere to report a logging failure; drop what was queued rather than grow without bound.
                while (_entries.Reader.TryRead(out _))
                {
                }
            }
        }
    }

    private void RollIfTooLarge()
    {
        var current = new FileInfo(_options.FilePath);
        if (!current.Exists || current.Length < _options.MaxFileBytes)
        {
            return;
        }

        for (var index = _options.RetainedFiles; index >= 1; index--)
        {
            var source = index == 1 ? _options.FilePath : RolledPath(index - 1);
            if (File.Exists(source))
            {
                File.Move(source, RolledPath(index), overwrite: true);
            }
        }

        // With nothing retained, the full file is simply started over.
        if (_options.RetainedFiles < 1)
        {
            File.Delete(_options.FilePath);
        }
    }

    private string RolledPath(int index) =>
        Path.ChangeExtension(_options.FilePath, $".{index.ToString(CultureInfo.InvariantCulture)}{Path.GetExtension(_options.FilePath)}");

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._options.MinimumLevel;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [").Append(Abbreviate(logLevel)).Append("] ")
                .Append(category).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null)
            {
                entry.AppendLine().Append(exception);
            }

            provider._entries.Writer.TryWrite(entry.ToString());
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
