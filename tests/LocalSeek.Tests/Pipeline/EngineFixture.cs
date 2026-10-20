using LocalSeek.Core.Index;
using LocalSeek.Indexing.Crawling;
using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.Extraction;
using LocalSeek.Indexing.Pipeline;
using LocalSeek.Indexing.Watching;
using LocalSeek.Tests.Fakes;
using LocalSeek.Tests.Index;
using Microsoft.Extensions.Time.Testing;

namespace LocalSeek.Tests.Pipeline;

/// <summary>An engine over a fake file system, fake index storage and fake clock.</summary>
public sealed class EngineFixture : IDisposable
{
    public const string Root = @"C:\root";

    public EngineFixture(FakeSourceFileSystem? fs = null, ExtractionOptions? extraction = null, IEnumerable<string>? exclusions = null)
    {
        FileSystem = fs ?? new FakeSourceFileSystem().Directory(Root);
        Time = new FakeTimeProvider(new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero));
        Diagnostics = new DiagnosticsLog(Time);
        Extractor = new ContentExtractor(extraction ?? new ExtractionOptions());
        Writer = IndexWriter.Open(Storage);
        Engine = new IndexingEngine(Writer, FileSystem, Extractor, Diagnostics, Time, new IndexingOptions
        {
            Roots = [Root],
            Exclusions = new ExclusionRules(exclusions ?? []),
            MaxParallelism = 2,
        });
    }

    public FakeSourceFileSystem FileSystem { get; }

    public FakeTimeProvider Time { get; }

    public DiagnosticsLog Diagnostics { get; }

    public ContentExtractor Extractor { get; }

    public FakeIndexStorage Storage { get; } = new();

    public IndexWriter Writer { get; }

    public IndexingEngine Engine { get; }

    public void Event(FileChangeKind kind, string path, string? oldPath = null) => Engine.OnFileChange(new FileChange(kind, path, oldPath));

    /// <summary>Lets the debounce interval pass and processes everything that became due.</summary>
    public async Task Settle(TimeSpan? wait = null)
    {
        Time.Advance(wait ?? TimeSpan.FromSeconds(1));
        await Engine.ProcessAsync();
    }

    public async Task ScanAll()
    {
        Engine.RequestFullScan();
        await Engine.ProcessAsync(includeNotYetDue: true);
    }

    public List<string> IndexedPaths() =>
        Writer.GetEntries().Select(e => e.Path).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public string Dump() => LogicalDump.Of(Writer);

    /// <summary>What a brand-new index built from the current file system contains.</summary>
    public async Task<string> RebuiltDump()
    {
        using var fresh = new EngineFixture(FileSystem, Extractor.Options);
        await fresh.ScanAll();
        return fresh.Dump();
    }

    public void Dispose() => Writer.Dispose();
}
