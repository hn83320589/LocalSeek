using LocalSeek.App.ViewModels;
using LocalSeek.Indexing.Settings;
using LocalSeek.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace LocalSeek.Tests.ViewModels;

/// <summary>End-to-end through the composition root with fake file system, watchers, clock and index storage.</summary>
public sealed class AppHostTests : IAsyncDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "localseek-host-" + Guid.NewGuid().ToString("N")));
    private readonly FakeSourceFileSystem _fs = new FakeSourceFileSystem()
        .File(@"C:\docs\apple.txt", "apple 台灣")
        .File(@"C:\docs\banana.md", "banana")
        .File(@"D:\other\cherry.txt", "cherry apple");
    private readonly FakeFileWatcherFactory _watchers = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly FakeIndexStorage _storage = new();
    private AppHost? _host;

    private AppHost Start(params string[] roots)
    {
        new SettingsStore(_paths.SettingsFile).Save(AppSettings.Default with { Roots = roots });
        _host = new AppHost(_paths, _fs, _watchers, _time, new QueueDispatcher { Immediate = true }, new FakeShell(), TimeZoneInfo.Utc, () => _storage);
        _host.Start();
        return _host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    private static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), because);
    }

    [Fact]
    public async Task Indexes_roots_and_searches_through_the_view_model()
    {
        var host = Start(@"C:\docs");
        await Eventually(() => host.Writer.GetEntries().Count == 2, "initial index");

        host.Main.Search.QueryText = "apple";
        _time.Advance(SearchViewModel.DebounceDelay);

        await Eventually(() => host.Main.Search.Results.Count == 1, "search results");
        Assert.Equal(@"C:\docs\apple.txt", host.Main.Search.Results[0].Path);
    }

    [Fact]
    public async Task Changing_roots_reindexes_and_drops_documents_outside_the_new_roots()
    {
        var host = Start(@"C:\docs");
        await Eventually(() => host.Writer.GetEntries().Count == 2, "initial index");

        await host.ApplySettingsAsync(AppSettings.Default with { Roots = [@"D:\other"] });

        await Eventually(() => host.Writer.GetEntries().Select(e => e.Path).SequenceEqual([@"D:\other\cherry.txt"]), "re-index with new roots");
    }

    [Fact]
    public void Missing_roots_and_damaged_settings_are_reported()
    {
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(_paths.SettingsFile, "{ broken");
        _host = new AppHost(_paths, _fs, _watchers, _time, new QueueDispatcher { Immediate = true }, new FakeShell(), TimeZoneInfo.Utc, () => _storage);

        Assert.Contains(_host.Main.Notifications, n => n.Contains("設定檔損毀", StringComparison.Ordinal));
        Assert.Contains(_host.Main.Notifications, n => n.Contains("尚未設定", StringComparison.Ordinal));
    }
}
