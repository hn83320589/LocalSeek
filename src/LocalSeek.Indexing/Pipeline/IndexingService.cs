using LocalSeek.Core.Index;
using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.FileSystem;
using LocalSeek.Indexing.Watching;

namespace LocalSeek.Indexing.Pipeline;

public sealed record IndexingStatus(bool Paused, bool Busy, int Pending, long Processed, double FilesPerSecond, IReadOnlyList<string> UnwatchedRoots);

/// <summary>
/// Runs an <see cref="IndexingEngine"/> in the background: one watcher per root (roots on network drives
/// or whose watcher cannot start fall back to a periodic difference scan), a start-up scan, debounced
/// processing, background merges, and pause/resume. All waiting goes through the injected TimeProvider.
/// </summary>
public sealed class IndexingService : IAsyncDisposable
{
    private readonly IndexingEngine _engine;
    private readonly IndexWriter _writer;
    private readonly IFileWatcherFactory _watcherFactory;
    private readonly ISourceFileSystem _fileSystem;
    private readonly DiagnosticsLog _diagnostics;
    private readonly TimeProvider _time;
    private readonly TimeSpan _periodicScan;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly List<IFileWatcher> _watchers = [];
    private readonly List<string> _unwatched = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private Task _merge = Task.CompletedTask;
    private volatile bool _paused;
    private volatile bool _busy;
    private long _lastProcessed;
    private DateTimeOffset _lastRateSample;
    private double _rate;

    public IndexingService(IndexingEngine engine, IndexWriter writer, IFileWatcherFactory watcherFactory, ISourceFileSystem fileSystem, DiagnosticsLog diagnostics, TimeProvider time, TimeSpan periodicScan, bool startPaused = false)
    {
        _engine = engine;
        _writer = writer;
        _watcherFactory = watcherFactory;
        _fileSystem = fileSystem;
        _diagnostics = diagnostics;
        _time = time;
        _periodicScan = periodicScan;
        _paused = startPaused;
    }

    public event EventHandler<IndexingStatus>? StatusChanged;

    /// <summary>Raised after work was committed (new results are searchable).</summary>
    public event EventHandler? Committed;

    /// <summary>Raised when processing failed unexpectedly (for example an index I/O error).</summary>
    public event EventHandler<Exception>? Faulted;

    public IndexingStatus Status => new(_paused, _busy, _engine.PendingCount, _engine.ProcessedCount, _rate, [.. _unwatched]);

    public void Start()
    {
        foreach (var root in _engine.Options.Roots)
        {
            StartWatching(root);
        }

        _engine.RequestFullScan();
        _lastRateSample = _time.GetUtcNow();
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public void Pause()
    {
        _paused = true;
        Publish();
    }

    public void Resume()
    {
        _paused = false;
        _signal.Release();
        Publish();
    }

    /// <summary>Asks for an immediate difference scan of every root.</summary>
    public void Rescan()
    {
        _engine.RequestFullScan();
        _signal.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        await _merge.ConfigureAwait(false);
        _stop.Dispose();
        _signal.Dispose();
    }

    private void StartWatching(string root)
    {
        if (_fileSystem.IsNetworkPath(root))
        {
            Fallback(root, "網路磁碟無法可靠監看，改以週期性差異掃描");
            return;
        }

        try
        {
            var watcher = _watcherFactory.Create(root);
            watcher.Changed += (_, change) =>
            {
                _engine.OnFileChange(change);
                _signal.Release();
            };
            watcher.Failed += (_, error) =>
            {
                _diagnostics.Record(root, DiagnosticKind.WatcherFallback, $"監看事件遺失（{error.Message}），已排程差異掃描");
                _engine.RequestScan(root);
                _signal.Release();
            };
            watcher.Start();
            _watchers.Add(watcher);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Fallback(root, $"無法監看此資料夾（{ex.Message}），改以週期性差異掃描");
        }
    }

    private void Fallback(string root, string message)
    {
        _unwatched.Add(root);
        _diagnostics.Record(root, DiagnosticKind.WatcherFallback, message);
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        var nextPeriodic = _time.GetUtcNow() + _periodicScan;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var now = _time.GetUtcNow();
                if (_unwatched.Count > 0 && now >= nextPeriodic)
                {
                    _unwatched.ForEach(_engine.RequestScan);
                    nextPeriodic = now + _periodicScan;
                }

                if (!_paused && _engine.NextDue is { } due && due <= now)
                {
                    _busy = true;
                    Publish();
                    await _engine.ProcessAsync(cancellation: cancellation).ConfigureAwait(false);
                    _busy = false;
                    UpdateRate();
                    Publish();
                    Committed?.Invoke(this, EventArgs.Empty);
                    StartBackgroundMerge(cancellation);
                    continue;
                }

                await WaitAsync(NextWakeUp(now, nextPeriodic), cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _busy = false;
                Faulted?.Invoke(this, ex);
                await WaitAsync(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan NextWakeUp(DateTimeOffset now, DateTimeOffset nextPeriodic)
    {
        var wait = Timeout.InfiniteTimeSpan;
        if (!_paused && _engine.NextDue is { } due)
        {
            wait = Max(due - now, TimeSpan.Zero);
        }

        if (_unwatched.Count > 0)
        {
            var periodic = Max(nextPeriodic - now, TimeSpan.Zero);
            wait = wait == Timeout.InfiniteTimeSpan || periodic < wait ? periodic : wait;
        }

        return wait;
    }

    private async Task WaitAsync(TimeSpan wait, CancellationToken cancellation)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var signal = _signal.WaitAsync(linked.Token);
            var delay = wait == Timeout.InfiniteTimeSpan ? Task.Delay(Timeout.Infinite, linked.Token) : Task.Delay(wait, _time, linked.Token);
            await Task.WhenAny(signal, delay).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            if (signal.IsCompletedSuccessfully)
            {
                // Collapse a burst of signals into one wake-up.
                while (_signal.CurrentCount > 0 && _signal.Wait(0))
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
    }

    private void StartBackgroundMerge(CancellationToken cancellation)
    {
        if (!_merge.IsCompleted)
        {
            return;
        }

        _merge = Task.Run(
            () =>
            {
                try
                {
                    while (!cancellation.IsCancellationRequested && _writer.MaybeMerge(cancellation))
                    {
                        Committed?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // Shutting down.
                }
                catch (Exception ex)
                {
                    Faulted?.Invoke(this, ex);
                }
            },
            CancellationToken.None);
    }

    private void UpdateRate()
    {
        var now = _time.GetUtcNow();
        var processed = _engine.ProcessedCount;
        var seconds = (now - _lastRateSample).TotalSeconds;
        if (seconds > 0)
        {
            _rate = (processed - _lastProcessed) / seconds;
        }

        _lastProcessed = processed;
        _lastRateSample = now;
    }

    private void Publish() => StatusChanged?.Invoke(this, Status);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
