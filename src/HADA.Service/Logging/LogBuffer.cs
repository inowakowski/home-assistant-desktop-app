using HADA.Ipc;

namespace HADA.Service.Logging;

/// <summary>Keeps the most recent log entries in memory so the settings window can show them.</summary>
public sealed class LogBuffer(int capacity = 1000)
{
    // Keeps a full page of entries well below the IPC message size limit.
    private const int MaxTextLength = 1500;

    private readonly Queue<LogEntry> _entries = new(capacity);
    private readonly Lock _gate = new();
    private long _lastSequence;

    public void Add(LogLevel level, string category, string message, Exception? exception)
    {
        lock (_gate)
        {
            if (_entries.Count == capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(new LogEntry(
                ++_lastSequence,
                DateTimeOffset.Now,
                level,
                category,
                Truncate(message),
                exception is null ? null : Truncate(exception.ToString())));
        }
    }

    /// <summary>
    /// Returns up to <paramref name="maxCount"/> entries newer than <paramref name="afterSequence"/>, oldest first.
    /// With <paramref name="afterSequence"/> 0 it returns the most recent entries instead of the oldest.
    /// </summary>
    public IReadOnlyList<LogEntry> GetAfter(long afterSequence, int maxCount)
    {
        lock (_gate)
        {
            return afterSequence <= 0
                ? _entries.TakeLast(maxCount).ToArray()
                : _entries.Where(entry => entry.Sequence > afterSequence).Take(maxCount).ToArray();
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxTextLength ? text : string.Concat(text.AsSpan(0, MaxTextLength), "…");
}
