using System.IO.Hashing;
using LocalSeek.Core.Index;
using LocalSeek.Indexing.Crawling;
using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.Extraction;
using LocalSeek.Indexing.FileSystem;
using LocalSeek.Indexing.Watching;

namespace LocalSeek.Indexing.Pipeline;

/// <summary>
/// Brings the index in line with the file system. Change sources (watcher events, overflow, start-up and
/// periodic scans) only mark paths or roots as dirty; <see cref="ProcessAsync"/> then looks at the
/// actual state of each dirty path and decides what to do, so lost, duplicated or reordered events
/// cannot corrupt the result:
/// <list type="number">
/// <item>gone (indexed, no longer on disk or no longer included) → delete;</item>
/// <item>new path whose indexed old path is gone with the same size and time (rename hint) or the same
/// content hash (inferred move) → rename in the index, without extracting or tokenizing the body;</item>
/// <item>new or modified → read, hash, extract and tokenize in parallel; a modified file whose bytes did
/// not change only gets its metadata updated.</item>
/// </list>
/// </summary>
public sealed class IndexingEngine
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PendingPath> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _renameHints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rootsToScan = new(StringComparer.OrdinalIgnoreCase);
    private readonly IndexWriter _writer;
    private readonly ISourceFileSystem _fileSystem;
    private readonly ContentExtractor _extractor;
    private readonly DiagnosticsLog _diagnostics;
    private readonly TimeProvider _time;
    private readonly Crawler _crawler;
    private readonly ThreadLocal<DocumentAnalyzer> _analyzers = new(() => new DocumentAnalyzer());
    private long _bodyAnalyses;
    private long _processed;
    private int _sinceCommit;

    public IndexingEngine(IndexWriter writer, ISourceFileSystem fileSystem, ContentExtractor extractor, DiagnosticsLog diagnostics, TimeProvider time, IndexingOptions options)
    {
        _writer = writer;
        _fileSystem = fileSystem;
        _extractor = extractor;
        _diagnostics = diagnostics;
        _time = time;
        Options = options;
        _crawler = new Crawler(fileSystem, diagnostics);
    }

    public IndexingOptions Options { get; }

    /// <summary>How many document bodies were tokenized (renames must not increase it).</summary>
    public long BodyAnalysisCount => Interlocked.Read(ref _bodyAnalyses);

    /// <summary>Files examined by reconciliation (indexed, skipped or found unchanged).</summary>
    public long ProcessedCount => Interlocked.Read(ref _processed);

    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count + _rootsToScan.Count;
            }
        }
    }

    /// <summary>When the next pending work becomes due, or null when idle.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_lock)
            {
                if (_rootsToScan.Count > 0)
                {
                    return _time.GetUtcNow();
                }

                return _pending.Count == 0 ? null : _pending.Values.Min(p => p.Due);
            }
        }
    }

    public void OnFileChange(FileChange change)
    {
        lock (_lock)
        {
            if (change is { Kind: FileChangeKind.Renamed, OldPath: not null })
            {
                _renameHints[change.Path] = change.OldPath;
                Touch(change.OldPath);
            }

            Touch(change.Path);
        }
    }

    /// <summary>Schedules a difference scan of a root (start-up, lost events, unwatchable roots).</summary>
    public void RequestScan(string root)
    {
        lock (_lock)
        {
            _rootsToScan.Add(root);
        }
    }

    public void RequestFullScan()
    {
        foreach (var root in Options.Roots)
        {
            RequestScan(root);
        }
    }

    /// <summary>Processes requested scans and every dirty path that is due (or all of them), then commits.</summary>
    public async Task<int> ProcessAsync(bool includeNotYetDue = false, CancellationToken cancellation = default)
    {
        string[] roots;
        List<string> paths;
        Dictionary<string, string> hints;
        Dictionary<string, int> lockAttempts;
        lock (_lock)
        {
            roots = [.. _rootsToScan];
            _rootsToScan.Clear();
            var now = _time.GetUtcNow();
            var due = _pending.Where(p => includeNotYetDue || p.Value.Due <= now).ToList();
            paths = due.Select(p => p.Key).ToList();
            lockAttempts = due.ToDictionary(p => p.Key, p => p.Value.LockAttempts, StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                _pending.Remove(path);
            }

            var dueSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            hints = _renameHints.Where(h => dueSet.Contains(h.Key) || dueSet.Contains(h.Value))
                .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var key in hints.Keys)
            {
                _renameHints.Remove(key);
            }
        }

        var handled = 0;
        foreach (var root in roots)
        {
            handled += await ScanRootAsync(root, cancellation).ConfigureAwait(false);
        }

        if (paths.Count > 0)
        {
            handled += await ApplyAsync(paths, hints, lockAttempts, known: null, cancellation).ConfigureAwait(false);
        }

        _writer.Commit();
        return handled;
    }

    private async Task<int> ScanRootAsync(string root, CancellationToken cancellation)
    {
        var known = new Dictionary<string, SourceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _crawler.Crawl(root, Options.Exclusions, Options.Crawl, cancellation: cancellation))
        {
            known[entry.FullPath] = entry;
        }

        var candidates = known.Keys.Concat(IndexedUnder(_writer.GetEntries(), root).Select(e => e.Path)).ToList();
        return await ApplyAsync(candidates, [], [], known, cancellation).ConfigureAwait(false);
    }

    private async Task<int> ApplyAsync(
        IReadOnlyCollection<string> paths,
        Dictionary<string, string> hints,
        Dictionary<string, int> lockAttempts,
        Dictionary<string, SourceEntry>? known,
        CancellationToken cancellation)
    {
        known ??= new Dictionary<string, SourceEntry>(StringComparer.OrdinalIgnoreCase);
        var indexedEntries = new Lazy<IReadOnlyList<IndexedEntry>>(_writer.GetEntries);
        var gone = new Dictionary<string, IndexedEntry>(StringComparer.OrdinalIgnoreCase);
        var work = new Dictionary<string, (SourceEntry Entry, IndexedEntry? Indexed)>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(paths);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = queue.Dequeue();
            if (!seen.Add(path))
            {
                continue;
            }

            _writer.TryGetEntry(path, out var indexed);
            var entry = known.TryGetValue(path, out var k) ? k : _fileSystem.GetEntry(path);
            if (entry is null)
            {
                if (indexed is not null)
                {
                    gone[path] = indexed;
                }
                else
                {
                    // Possibly a deleted or moved-away directory: everything indexed below it is gone.
                    foreach (var child in IndexedUnder(indexedEntries.Value, path))
                    {
                        queue.Enqueue(child.Path);
                    }
                }

                continue;
            }

            if (entry.IsDirectory)
            {
                foreach (var child in IndexedUnder(indexedEntries.Value, path))
                {
                    queue.Enqueue(child.Path);
                }

                var root = RootOf(path);
                if (root is not null && IsIncluded(root, path, entry))
                {
                    hints.TryGetValue(path, out var oldDirectory);
                    foreach (var file in _crawler.Crawl(root, Options.Exclusions, Options.Crawl, cancellation: cancellation, startDirectory: path))
                    {
                        known[file.FullPath] = file;
                        queue.Enqueue(file.FullPath);
                        if (oldDirectory is not null)
                        {
                            var oldFile = oldDirectory + file.FullPath[path.Length..];
                            hints[file.FullPath] = oldFile;
                            queue.Enqueue(oldFile);
                        }
                    }
                }

                continue;
            }

            var fileRoot = RootOf(path);
            if (fileRoot is null || !IsIncluded(fileRoot, path, entry))
            {
                if (indexed is not null)
                {
                    gone[path] = indexed;
                }

                continue;
            }

            if (indexed is not null && indexed.Size == entry.Size && indexed.LastWriteUtc == entry.LastWriteUtc)
            {
                Interlocked.Increment(ref _processed);
                continue;
            }

            work[path] = (entry, indexed);
        }

        ApplyRenameHints(hints, gone, work);
        var preRead = ApplyInferredMoves(gone, work);

        foreach (var (path, _) in gone)
        {
            _writer.Delete(path);
            _diagnostics.Clear(path);
        }

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = Options.MaxParallelism, CancellationToken = cancellation },
            (item, _) =>
            {
                ProcessFile(item.Key, item.Value.Entry, item.Value.Indexed, preRead.GetValueOrDefault(item.Key), lockAttempts.GetValueOrDefault(item.Key));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        return seen.Count;
    }

    /// <summary>Watcher-reported renames: same size and time means the bytes did not change.</summary>
    private void ApplyRenameHints(Dictionary<string, string> hints, Dictionary<string, IndexedEntry> gone, Dictionary<string, (SourceEntry Entry, IndexedEntry? Indexed)> work)
    {
        foreach (var (newPath, oldPath) in hints)
        {
            if (work.TryGetValue(newPath, out var target) && target.Indexed is null
                && gone.TryGetValue(oldPath, out var old)
                && old.Size == target.Entry.Size && old.LastWriteUtc == target.Entry.LastWriteUtc)
            {
                _writer.Rename(oldPath, newPath, target.Entry.Size, target.Entry.LastWriteUtc);
                MoveDiagnostics(oldPath, newPath);
                work.Remove(newPath);
                gone.Remove(oldPath);
                Interlocked.Increment(ref _processed);
            }
        }
    }

    /// <summary>
    /// Moves reported as delete + create (or found by a difference scan): a new file with the same size
    /// and content hash as a gone one is the same document. The bytes read here are reused if it is not.
    /// </summary>
    private Dictionary<string, ReadOutcome> ApplyInferredMoves(Dictionary<string, IndexedEntry> gone, Dictionary<string, (SourceEntry Entry, IndexedEntry? Indexed)> work)
    {
        var preRead = new Dictionary<string, ReadOutcome>(StringComparer.OrdinalIgnoreCase);
        if (gone.Count == 0)
        {
            return preRead;
        }

        var goneBySize = gone.Values.GroupBy(g => g.Size).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (path, (entry, indexed)) in work.ToList())
        {
            if (indexed is not null || !goneBySize.TryGetValue(entry.Size, out var candidates) || candidates.Count == 0 || _extractor.IsTooLarge(entry.Size))
            {
                continue;
            }

            var outcome = SourceFileReader.Read(_fileSystem, path);
            preRead[path] = outcome;
            if (outcome is not ReadOutcome.Success success)
            {
                continue;
            }

            var hash = XxHash3.HashToUInt64(success.Bytes);
            var match = candidates.FirstOrDefault(c => c.ContentHash == hash);
            if (match is null)
            {
                continue;
            }

            _writer.Rename(match.Path, path, success.Entry.Size, success.Entry.LastWriteUtc);
            MoveDiagnostics(match.Path, path);
            candidates.Remove(match);
            gone.Remove(match.Path);
            work.Remove(path);
            preRead.Remove(path);
            Interlocked.Increment(ref _processed);
        }

        return preRead;
    }

    private void ProcessFile(string path, SourceEntry entry, IndexedEntry? indexed, ReadOutcome? preRead, int lockAttempts)
    {
        Interlocked.Increment(ref _processed);
        if (_extractor.IsTooLarge(entry.Size))
        {
            Skip(path, indexed, DiagnosticKind.TooLarge, $"檔案大小 {entry.Size:N0} bytes 超過上限 {_extractor.Options.MaxFileBytes:N0} bytes");
            return;
        }

        switch (preRead ?? SourceFileReader.Read(_fileSystem, path))
        {
            case ReadOutcome.Success success:
                Index(path, success, indexed);
                break;
            case ReadOutcome.Locked locked:
                ScheduleLockRetry(path, lockAttempts, locked.Message);
                break;
            case ReadOutcome.Changed:
                lock (_lock)
                {
                    Touch(path);
                }

                break;
            case ReadOutcome.Missing:
                if (indexed is not null)
                {
                    _writer.Delete(path);
                }

                _diagnostics.Clear(path);
                break;
            case ReadOutcome.AccessDenied denied:
                _diagnostics.Record(path, DiagnosticKind.AccessDenied, $"權限不足，無法讀取：{denied.Message}");
                break;
            case ReadOutcome.Failed failed:
                _diagnostics.Record(path, DiagnosticKind.ReadError, $"讀取失敗：{failed.Message}");
                break;
        }
    }

    private void Index(string path, ReadOutcome.Success read, IndexedEntry? indexed)
    {
        var hash = XxHash3.HashToUInt64(read.Bytes);
        if (indexed is not null && indexed.ContentHash == hash)
        {
            // Only the timestamp changed: refresh metadata, keep the tokenized body.
            _writer.Rename(path, path, read.Entry.Size, read.Entry.LastWriteUtc);
            _diagnostics.Clear(path);
            return;
        }

        var extracted = _extractor.Extract(path, read.Bytes);
        if (extracted is ExtractionResult.Skipped skipped)
        {
            Skip(path, indexed, DiagnosticsLog.KindOf(skipped.Reason), skipped.Message);
            return;
        }

        var analyzer = _analyzers.Value!;
        var body = analyzer.Analyze(((ExtractionResult.Success)extracted).Text);
        Interlocked.Increment(ref _bodyAnalyses);
        var name = analyzer.Analyze(DocumentAnalyzer.FileNameOf(path));
        _writer.AddOrUpdate(new PreparedDocument(path, read.Entry.Size, read.Entry.LastWriteUtc, hash, body, name));
        _diagnostics.Clear(path);

        if (Interlocked.Increment(ref _sinceCommit) >= Options.CommitInterval)
        {
            Interlocked.Exchange(ref _sinceCommit, 0);
            _writer.Commit();
        }
    }

    /// <summary>A skipped file is not indexed at all (not even its name).</summary>
    private void Skip(string path, IndexedEntry? indexed, DiagnosticKind kind, string message)
    {
        if (indexed is not null)
        {
            _writer.Delete(path);
        }

        _diagnostics.Record(path, kind, message);
    }

    private void ScheduleLockRetry(string path, int attempts, string message)
    {
        var delays = Options.LockRetryDelays;
        if (attempts >= delays.Count)
        {
            _diagnostics.Record(path, DiagnosticKind.LockedGaveUp, $"檔案持續被其他程式鎖定（已重試 {attempts} 次），待檔案變更或下次掃描再試：{message}");
            return;
        }

        _diagnostics.Record(path, DiagnosticKind.LockedRetryLater, $"檔案被其他程式鎖定，稍後重試（第 {attempts + 1} 次）");
        lock (_lock)
        {
            var due = _time.GetUtcNow() + delays[attempts];
            _pending[path] = new PendingPath(due, due, attempts + 1);
        }
    }

    private void MoveDiagnostics(string from, string to)
    {
        _diagnostics.Clear(from);
        _diagnostics.Clear(to);
    }

    /// <summary>Marks a path dirty: due after the debounce interval, but no later than MaxDelay after its first event.</summary>
    private void Touch(string path)
    {
        var now = _time.GetUtcNow();
        var due = now + Options.Debounce;
        _pending[path] = _pending.TryGetValue(path, out var existing)
            ? existing with { Due = Min(due, existing.FirstSeen + Options.MaxDelay), LockAttempts = 0 }
            : new PendingPath(due, now, 0);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private string? RootOf(string path) =>
        Options.Roots.Where(r => IsSameOrUnder(path, r)).MaxBy(r => r.Length);

    /// <summary>Applies the same rules as the crawler to a single path (and its ancestors).</summary>
    private bool IsIncluded(string root, string path, SourceEntry entry)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return entry.IsDirectory;
        }

        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var parts = relative.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            var ancestorRelative = string.Join('/', parts[..i]);
            if (Options.Exclusions.IsExcluded(ancestorRelative, isDirectory: true))
            {
                return false;
            }

            if (Options.Crawl.SkipHiddenAndSystem && _fileSystem.GetEntry(Path.Combine(root, ancestorRelative.Replace('/', '\\'))) is { IsHiddenOrSystem: true })
            {
                return false;
            }
        }

        if (Options.Exclusions.IsExcluded(relative, entry.IsDirectory))
        {
            return false;
        }

        if (Options.Crawl.SkipHiddenAndSystem && entry.IsHiddenOrSystem)
        {
            _diagnostics.Record(path, DiagnosticKind.HiddenOrSystem, "隱藏或系統屬性，依設定略過");
            return false;
        }

        return entry.IsDirectory || ContentExtractor.IsSupported(path);
    }

    private static IEnumerable<IndexedEntry> IndexedUnder(IEnumerable<IndexedEntry> entries, string directory) =>
        entries.Where(e => e.Path.Length > directory.Length && IsSameOrUnder(e.Path, directory));

    private static bool IsSameOrUnder(string path, string directory) =>
        path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || (path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) && path.Length > directory.Length && path[directory.Length] == '\\');

    private sealed record PendingPath(DateTimeOffset Due, DateTimeOffset FirstSeen, int LockAttempts);
}
