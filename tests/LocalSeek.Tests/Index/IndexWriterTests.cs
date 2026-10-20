using LocalSeek.Core.Index;
using LocalSeek.Core.Query;
using LocalSeek.Core.Search;
using LocalSeek.Core.Storage;
using LocalSeek.Tests.Fakes;

namespace LocalSeek.Tests.Index;

public class IndexWriterTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static DocumentInput Doc(string path, string body, ulong hash = 0, long size = 10) => new(path, size, T0, body, hash);

    private static List<string> Search(IndexWriter writer, string query)
    {
        using var snapshot = writer.AcquireSnapshot();
        return QueryExecutor.Execute(snapshot, QueryBinder.Bind(QueryParser.Parse(query)), SearchOptions.Default)
            .Hits.Select(h => h.Path).ToList();
    }

    [Fact]
    public void Committed_documents_survive_reopen_and_match_memory_index()
    {
        var storage = new FakeIndexStorage();
        var docs = new[] { Doc(@"C:\a\報告.txt", "台灣大學 AI模型 apple"), Doc(@"C:\a\b.md", "banana apple 資料"), Doc(@"C:\b\空.txt", "") };
        using (var writer = IndexWriter.Open(storage))
        {
            foreach (var d in docs)
            {
                writer.AddOrUpdate(d);
            }

            writer.Commit();
        }

        using var reopened = IndexWriter.Open(storage);
        Assert.False(reopened.OpenReport.HasIssues);
        Assert.Equal(LogicalDump.Of(MemoryIndex.Build(docs)), LogicalDump.Of(reopened));
    }

    [Fact]
    public void Uncommitted_changes_are_invisible_and_lost_on_reopen()
    {
        var storage = new FakeIndexStorage();
        using (var writer = IndexWriter.Open(storage))
        {
            writer.AddOrUpdate(Doc(@"C:\a.txt", "apple"));
            Assert.Empty(Search(writer, "apple"));
        }

        using var reopened = IndexWriter.Open(storage);
        Assert.Empty(Search(reopened, "apple"));
    }

    [Fact]
    public void Update_replaces_document_with_same_path_ignoring_case()
    {
        using var writer = IndexWriter.Open(new FakeIndexStorage());
        writer.AddOrUpdate(Doc(@"C:\A.txt", "apple"));
        writer.Commit();
        writer.AddOrUpdate(Doc(@"c:\a.TXT", "banana"));
        writer.Commit();

        Assert.Empty(Search(writer, "apple"));
        Assert.Equal([@"c:\a.TXT"], Search(writer, "banana"));
    }

    [Fact]
    public void Delete_removes_document()
    {
        using var writer = IndexWriter.Open(new FakeIndexStorage());
        writer.AddOrUpdate(Doc(@"C:\a.txt", "apple"));
        writer.AddOrUpdate(Doc(@"C:\b.txt", "apple"));
        writer.Commit();

        Assert.True(writer.Delete(@"C:\A.TXT"));
        Assert.False(writer.Delete(@"C:\missing.txt"));
        writer.Commit();

        Assert.Equal([@"C:\b.txt"], Search(writer, "apple"));
    }

    [Fact]
    public void Rename_reuses_body_and_reindexes_only_the_name()
    {
        var storage = new FakeIndexStorage();
        using var writer = IndexWriter.Open(storage);
        writer.AddOrUpdate(Doc(@"C:\old\notes.txt", "台灣大學 apple", hash: 42));
        writer.Commit();

        Assert.True(writer.Rename(@"C:\old\notes.txt", @"C:\new\報告.txt", 10, T0));
        writer.Commit();

        Assert.Equal([@"C:\new\報告.txt"], Search(writer, "台灣大學"));
        Assert.Equal([@"C:\new\報告.txt"], Search(writer, "報告"));
        Assert.Empty(Search(writer, "notes"));
        Assert.True(writer.TryGetEntry(@"C:\new\報告.txt", out var entry));
        Assert.Equal(42UL, entry.ContentHash);
        Assert.False(writer.TryGetEntry(@"C:\old\notes.txt", out _));

        var expected = MemoryIndex.Build([Doc(@"C:\new\報告.txt", "台灣大學 apple")]);
        Assert.Equal(LogicalDump.Of(expected), LogicalDump.Of(writer));
        using var reopened = IndexWriter.Open(storage);
        Assert.Equal(LogicalDump.Of(expected), LogicalDump.Of(reopened));
    }

    [Fact]
    public void Rename_of_uncommitted_document_and_onto_existing_path()
    {
        using var writer = IndexWriter.Open(new FakeIndexStorage());
        writer.AddOrUpdate(Doc(@"C:\target.txt", "banana"));
        writer.AddOrUpdate(Doc(@"C:\source.txt", "apple"));
        Assert.True(writer.Rename(@"C:\source.txt", @"C:\target.txt", 10, T0));
        Assert.False(writer.Rename(@"C:\nope.txt", @"C:\x.txt", 10, T0));
        writer.Commit();

        Assert.Equal(LogicalDump.Of(MemoryIndex.Build([Doc(@"C:\target.txt", "apple")])), LogicalDump.Of(writer));
    }

    [Fact]
    public void Small_ram_buffer_produces_several_segments_with_same_content()
    {
        var docs = Enumerable.Range(0, 60).Select(i => Doc($@"C:\d\{i}.txt", $"apple 台灣 doc{i} 資料{i % 7}")).ToList();
        using var writer = IndexWriter.Open(new FakeIndexStorage(), new IndexWriterOptions { RamBufferBytes = 2000 });
        docs.ForEach(writer.AddOrUpdate);
        writer.Commit();

        Assert.True(writer.GetStatistics().SegmentCount > 3);
        Assert.Equal(LogicalDump.Of(MemoryIndex.Build(docs)), LogicalDump.Of(writer));
    }

    [Fact]
    public void Fully_deleted_segment_is_dropped_and_its_file_removed_once_commits_roll_off()
    {
        var storage = new FakeIndexStorage();
        using var writer = IndexWriter.Open(storage);
        writer.AddOrUpdate(Doc(@"C:\a.txt", "apple"));
        writer.Commit();
        var segment = Assert.Single(storage.Files("seg_", ".lss"));

        writer.Delete(@"C:\a.txt");
        writer.Commit();
        Assert.Equal(0, writer.GetStatistics().SegmentCount);
        Assert.True(storage.Exists(segment), "Still referenced by the retained previous commit.");

        writer.AddOrUpdate(Doc(@"C:\b.txt", "banana"));
        writer.Commit();
        Assert.False(storage.Exists(segment));
        Assert.Equal(2, storage.Files("manifest_", ".lsm").Count);
    }

    [Fact]
    public void Commit_without_changes_keeps_generation()
    {
        using var writer = IndexWriter.Open(new FakeIndexStorage());
        writer.AddOrUpdate(Doc(@"C:\a.txt", "apple"));
        var generation = writer.Commit();

        Assert.Equal(generation, writer.Commit());
    }

    [Fact]
    public void Entries_expose_metadata_and_content_hash_including_uncommitted()
    {
        using var writer = IndexWriter.Open(new FakeIndexStorage());
        writer.AddOrUpdate(Doc(@"C:\a.txt", "apple", hash: 7, size: 123));
        writer.Commit();
        writer.AddOrUpdate(Doc(@"C:\b.txt", "apple", hash: 8));

        Assert.Equal(
            [new IndexedEntry(@"C:\a.txt", 123, T0, 7), new IndexedEntry(@"C:\b.txt", 10, T0, 8)],
            writer.GetEntries().OrderBy(e => e.Path));
    }

    [Fact]
    public void Works_on_the_real_file_system()
    {
        var directory = Path.Combine(Path.GetTempPath(), "localseek-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = IndexWriter.Open(new FileSystemIndexStorage(directory)))
            {
                writer.AddOrUpdate(Doc(@"C:\a.txt", "台灣 apple"));
                writer.Commit();
                writer.AddOrUpdate(Doc(@"C:\b.txt", "banana"));
                writer.Delete(@"C:\a.txt");
                writer.Commit();
            }

            using var reopened = IndexWriter.Open(new FileSystemIndexStorage(directory));
            Assert.False(reopened.OpenReport.HasIssues);
            Assert.Equal(LogicalDump.Of(MemoryIndex.Build([Doc(@"C:\b.txt", "banana")])), LogicalDump.Of(reopened));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
