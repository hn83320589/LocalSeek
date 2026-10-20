using System.Collections.Concurrent;
using LocalSeek.Indexing.Extraction;

namespace LocalSeek.Indexing.Diagnostics;

public enum DiagnosticKind
{
    TooLarge,
    Binary,
    UnknownEncoding,
    Corrupt,
    AccessDenied,
    LockedRetryLater,
    LockedGaveUp,
    ChangedDuringRead,
    ReadError,
    LoopDetected,
    BrokenLink,
    ReparsePointSkipped,
    HiddenOrSystem,
    RootUnavailable,
    WatcherFallback,
}

public sealed record DiagnosticEntry(string Path, DiagnosticKind Kind, string Message, DateTime TimeUtc);

/// <summary>
/// The current set of skipped or failed paths, one entry per path (the latest reason wins). An entry is
/// cleared when the path is later indexed successfully or disappears, so the diagnostics page always
/// matches reality. Thread-safe.
/// </summary>
public sealed class DiagnosticsLog(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, DiagnosticEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public int Count => _entries.Count;

    public static DiagnosticKind KindOf(SkipReason reason) => reason switch
    {
        SkipReason.TooLarge => DiagnosticKind.TooLarge,
        SkipReason.Binary => DiagnosticKind.Binary,
        SkipReason.UnknownEncoding => DiagnosticKind.UnknownEncoding,
        _ => DiagnosticKind.Corrupt,
    };

    public void Record(string path, DiagnosticKind kind, string message)
    {
        _entries[path] = new DiagnosticEntry(path, kind, message, time.GetUtcNow().UtcDateTime);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(string path)
    {
        if (_entries.TryRemove(path, out _))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Removes entries for a directory and everything below it.</summary>
    public void ClearUnder(string directory)
    {
        var prefix = directory.TrimEnd('\\') + '\\';
        var removed = false;
        foreach (var path in _entries.Keys)
        {
            if (path.Equals(directory, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                removed |= _entries.TryRemove(path, out _);
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool TryGet(string path, out DiagnosticEntry entry) => _entries.TryGetValue(path, out entry!);

    public IReadOnlyList<DiagnosticEntry> Entries() =>
        _entries.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyDictionary<DiagnosticKind, int> CountsByKind() =>
        _entries.Values.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
}
