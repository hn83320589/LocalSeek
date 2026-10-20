using LocalSeek.Core.Index;
using LocalSeek.Core.Storage;
using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.Extraction;
using LocalSeek.Indexing.FileSystem;
using LocalSeek.Indexing.Pipeline;
using LocalSeek.Indexing.Preview;
using LocalSeek.Indexing.Settings;
using LocalSeek.Indexing.Watching;

namespace LocalSeek.App.ViewModels;

/// <summary>
/// Composition root without UI: settings, index writer, indexing service, search and preview, and the
/// view models. Restarts indexing when settings change and reopens the index after an I/O failure.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly ISourceFileSystem _fileSystem;
    private readonly IFileWatcherFactory _watchers;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _ui;
    private readonly Func<IIndexStorage> _storageFactory;
    private readonly DiagnosticsLog _diagnostics;
    private readonly SettingsStore _store;
    private AppSettings _settings = AppSettings.Default;
    private IndexWriter _writer = null!;
    private IndexingService? _service;
    private ContentExtractor _extractor = null!;

    public AppHost(AppPaths paths, ISourceFileSystem fileSystem, IFileWatcherFactory watchers, TimeProvider time, IUiDispatcher ui, IShellService shell, TimeZoneInfo timeZone, Func<IIndexStorage>? storageFactory = null)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _watchers = watchers;
        _time = time;
        _ui = ui;
        _storageFactory = storageFactory ?? (() => new FileSystemIndexStorage(paths.IndexDirectory));
        _diagnostics = new DiagnosticsLog(time);
        _store = new SettingsStore(paths.SettingsFile);

        var loaded = _store.Load();
        _settings = loaded.Settings;
        _extractor = new ContentExtractor(new ExtractionOptions { MaxFileBytes = _settings.MaxFileBytes });
        _writer = IndexWriter.Open(_storageFactory());

        var search = new SearchViewModel(
            new IndexSearchService(() => _writer.AcquireSnapshot(), timeZone),
            new PreviewService(new DocumentPreviewer(fileSystem, _extractor)),
            shell,
            ui,
            time,
            timeZone);
        var settings = new SettingsViewModel(_store, _settings, loaded.Warning);
        settings.Saved += (_, updated) => _ = ApplySettingsAsync(updated);
        var diagnostics = new DiagnosticsViewModel(_diagnostics, () => _writer.GetStatistics(), timeZone);
        diagnostics.SetOpenReport(_writer.OpenReport);
        Main = new MainViewModel(search, new StatusViewModel(ui, () => _writer.GetStatistics()), diagnostics, settings);

        if (loaded.Warning is not null)
        {
            Main.Notify(loaded.Warning);
        }

        foreach (var issue in _writer.OpenReport.Issues)
        {
            Main.Notify($"索引檢查：{issue.Message}");
        }

        if (_settings.Roots.Count == 0)
        {
            Main.Notify("尚未設定要索引的資料夾，請到「設定」加入根資料夾。");
        }
    }

    public MainViewModel Main { get; }

    public IndexWriter Writer => _writer;

    public DiagnosticsLog Diagnostics => _diagnostics;

    public void Start() => StartIndexing();

    public async Task ApplySettingsAsync(AppSettings settings)
    {
        _settings = settings;
        await StopIndexingAsync().ConfigureAwait(true);
        _extractor = new ContentExtractor(new ExtractionOptions { MaxFileBytes = settings.MaxFileBytes });
        StartIndexing();
    }

    public async ValueTask DisposeAsync()
    {
        await StopIndexingAsync().ConfigureAwait(false);
        Main.Search.Dispose();
        _writer.Dispose();
    }

    private void StartIndexing()
    {
        var engine = new IndexingEngine(_writer, _fileSystem, _extractor, _diagnostics, _time, IndexingOptions.From(_settings));
        var service = new IndexingService(engine, _writer, _watchers, _fileSystem, _diagnostics, _time, TimeSpan.FromMinutes(_settings.PeriodicScanMinutes), _settings.IndexingPaused);
        service.Committed += (_, _) => _ui.Post(Main.OnIndexChanged);
        service.Faulted += (_, error) => _ui.Post(() => _ = RecoverAsync(service, error));
        _service = service;
        Main.Status.Attach(service);
        service.Start();
    }

    private async Task StopIndexingAsync()
    {
        if (_service is { } service)
        {
            _service = null;
            await service.DisposeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>An index I/O error poisons the writer; reopening recovers to the last commit and rescans.</summary>
    private async Task RecoverAsync(IndexingService failed, Exception error)
    {
        if (!ReferenceEquals(failed, _service))
        {
            return;
        }

        Main.Notify($"索引發生錯誤，正在重新開啟索引：{error.Message}");
        await StopIndexingAsync().ConfigureAwait(true);
        _writer.Dispose();
        _writer = IndexWriter.Open(_storageFactory());
        Main.Diagnostics.SetOpenReport(_writer.OpenReport);
        StartIndexing();
    }
}
