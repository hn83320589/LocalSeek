using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.Pipeline;
using LocalSeek.Indexing.Watching;
using LocalSeek.Tests.Fakes;
using static LocalSeek.Tests.Pipeline.EngineFixture;

namespace LocalSeek.Tests.Pipeline;

public sealed class IndexingServiceTests : IAsyncDisposable
{
    private readonly EngineFixture _f = new(new FakeSourceFileSystem().File($@"{Root}\a.txt", "apple"));
    private readonly FakeFileWatcherFactory _watchers = new();
    private IndexingService? _service;

    private IndexingService Start(bool paused = false)
    {
        _service = new IndexingService(_f.Engine, _f.Writer, _watchers, _f.FileSystem, _f.Diagnostics, _f.Time, TimeSpan.FromMinutes(5), paused);
        _service.Start();
        return _service;
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.DisposeAsync();
        }

        _f.Dispose();
    }

    /// <summary>Waits (in real time) for the background loop; the fake clock is advanced by the test.</summary>
    private static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), because);
    }

    [Fact]
    public async Task Start_scans_roots_and_watches_them()
    {
        Start();

        await Eventually(() => _f.IndexedPaths().Count == 1, "initial scan");
        Assert.True(_watchers.Watchers[Root].Started);
    }

    [Fact]
    public async Task Watched_change_becomes_searchable_after_the_debounce()
    {
        Start();
        await Eventually(() => _f.IndexedPaths().Count == 1, "initial scan");

        _f.FileSystem.File($@"{Root}\b.txt", "banana");
        _watchers.Watchers[Root].Raise(new FileChange(FileChangeKind.Created, $@"{Root}\b.txt"));
        await Task.Delay(50);
        Assert.Single(_f.IndexedPaths());

        _f.Time.Advance(TimeSpan.FromMilliseconds(500));
        await Eventually(() => _f.IndexedPaths().Count == 2, "file indexed once the debounce elapsed");
    }

    [Fact]
    public async Task Watcher_overflow_falls_back_to_a_difference_scan()
    {
        Start();
        await Eventually(() => _f.IndexedPaths().Count == 1, "initial scan");

        _f.FileSystem.File($@"{Root}\silent.txt", "no event for this one");
        _watchers.Watchers[Root].Overflow();

        await Eventually(() => _f.IndexedPaths().Count == 2, "overflow scan");
        Assert.True(_f.Diagnostics.TryGet(Root, out var entry));
        Assert.Equal(DiagnosticKind.WatcherFallback, entry.Kind);
    }

    [Fact]
    public async Task Unwatchable_root_is_scanned_periodically()
    {
        _watchers.Unwatchable.Add(Root);
        var service = Start();
        await Eventually(() => _f.IndexedPaths().Count == 1, "initial scan");
        Assert.Equal([Root], service.Status.UnwatchedRoots);

        _f.FileSystem.File($@"{Root}\later.txt", "added without events");
        _f.Time.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(50);
        Assert.Single(_f.IndexedPaths());

        _f.Time.Advance(TimeSpan.FromMinutes(1));
        await Eventually(() => _f.IndexedPaths().Count == 2, "periodic scan");
    }

    [Fact]
    public async Task Network_root_uses_periodic_scans()
    {
        _f.FileSystem.NetworkRoots.Add(Root);
        var service = Start();

        await Eventually(() => _f.IndexedPaths().Count == 1, "initial scan");
        Assert.Empty(_watchers.Watchers);
        Assert.Equal([Root], service.Status.UnwatchedRoots);
    }

    [Fact]
    public async Task Paused_service_collects_changes_and_processes_them_on_resume()
    {
        var service = Start(paused: true);
        await Task.Delay(50);
        Assert.Empty(_f.IndexedPaths());
        Assert.True(service.Status.Paused);

        service.Resume();

        await Eventually(() => _f.IndexedPaths().Count == 1, "processing after resume");
        Assert.False(service.Status.Paused);
    }

    [Fact]
    public void Options_are_derived_from_settings()
    {
        var options = IndexingOptions.From(Indexing.Settings.AppSettings.Default with
        {
            Roots = [@"D:\docs\"],
            ExcludePatterns = ["**/*.tmp"],
            DebounceMilliseconds = 800,
            SkipHiddenAndSystem = false,
        });

        Assert.Equal([@"D:\docs"], options.Roots);
        Assert.True(options.Exclusions.IsExcluded("x/a.tmp", false));
        Assert.Equal(TimeSpan.FromMilliseconds(800), options.Debounce);
        Assert.False(options.Crawl.SkipHiddenAndSystem);
    }
}
