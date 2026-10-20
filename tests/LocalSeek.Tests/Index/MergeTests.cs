using LocalSeek.Core.Index;
using LocalSeek.Tests.Fakes;
using static LocalSeek.Tests.Index.IndexWriterTests;

namespace LocalSeek.Tests.Index;

public class MergeTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IndexWriterOptions Options = new()
    {
        RamBufferBytes = 1500,
        MergePolicy = new MergePolicy { MergeFactor = 3, MinSegmentDocs = 4 },
    };

    private static List<DocumentInput> Fill(IndexWriter writer, int count, string prefix = "d")
    {
        var docs = Enumerable.Range(0, count).Select(i => Doc($@"C:\{prefix}\{i}.txt", $"apple 台灣大學 {prefix}{i} 資料{i % 5}", hash: (ulong)i)).ToList();
        foreach (var d in docs)
        {
            writer.AddOrUpdate(d);
        }

        writer.Commit();
        return docs;
    }

    [Fact]
    public void Force_merge_keeps_logical_content_and_clears_tombstones()
    {
        using var writer = IndexWriter.Open(new FakeIndexStorage(), Options);
        var docs = Fill(writer, 40);
        writer.Delete(@"C:\d\3.txt");
        writer.Delete(@"C:\d\17.txt");
        writer.Commit();
        var before = LogicalDump.Of(writer);
        Assert.True(writer.GetStatistics().DeletedDocuments > 0);

        Assert.True(writer.ForceMerge());

        var stats = writer.GetStatistics();
        Assert.Equal(1, stats.SegmentCount);
        Assert.Equal(0, stats.DeletedDocuments);
        Assert.Equal(38, stats.LiveDocuments);
        Assert.Equal(before, LogicalDump.Of(writer));
        Assert.False(writer.ForceMerge(), "Nothing left to merge.");
    }

    [Fact]
    public void Merged_index_survives_reopen()
    {
        var storage = new FakeIndexStorage();
        string before;
        using (var writer = IndexWriter.Open(storage, Options))
        {
            Fill(writer, 30);
            writer.ForceMerge();
            before = LogicalDump.Of(writer);
        }

        using var reopened = IndexWriter.Open(storage, Options);
        Assert.False(reopened.OpenReport.HasIssues);
        Assert.Equal(before, LogicalDump.Of(reopened));
        Assert.Equal(1, reopened.GetStatistics().SegmentCount);
    }

    [Fact]
    public void Merge_moves_renamed_documents_with_their_content()
    {
        using var writer = IndexWriter.Open(new FakeIndexStorage(), Options);
        Fill(writer, 10);
        writer.Rename(@"C:\d\1.txt", @"C:\moved\一.txt", 10, T0);
        writer.Rename(@"C:\d\2.txt", @"C:\moved\二.txt", 10, T0);
        writer.Commit();
        var before = LogicalDump.Of(writer);

        Assert.True(writer.ForceMerge());

        Assert.Equal(before, LogicalDump.Of(writer));
        Assert.Equal(1, writer.GetStatistics().SegmentCount);
    }

    [Fact]
    public void Expunge_merge_of_a_content_only_segment_pulls_in_the_owners()
    {
        var options = Options with { RamBufferBytes = 1 << 20, MergePolicy = new MergePolicy { MergeFactor = 10, MinSegmentDocs = 4, ExpungeDeletesRatio = 0.5 } };
        using var writer = IndexWriter.Open(new FakeIndexStorage(), options);
        Fill(writer, 4);
        foreach (var i in Enumerable.Range(0, 4))
        {
            writer.Rename($@"C:\d\{i}.txt", $@"C:\moved\{i}.txt", 10, T0);
        }

        writer.Commit();
        var before = LogicalDump.Of(writer);
        Assert.Equal(2, writer.GetStatistics().SegmentCount);

        Assert.True(writer.MaybeMerge());

        Assert.Equal(before, LogicalDump.Of(writer));
        Assert.Equal(1, writer.GetStatistics().SegmentCount);
    }

    [Fact]
    public void Changes_made_while_a_merge_runs_are_carried_over()
    {
        var storage = new FakeIndexStorage();
        using var writer = IndexWriter.Open(storage, Options);
        var docs = Fill(writer, 20).ToDictionary(d => d.Path);
        var fired = false;
        storage.OnCreate = name =>
        {
            if (fired || !name.StartsWith("seg_", StringComparison.Ordinal))
            {
                return;
            }

            fired = true;
            writer.Delete(@"C:\d\5.txt");
            writer.Rename(@"C:\d\6.txt", @"C:\moved\6.txt", 10, T0);
            writer.AddOrUpdate(Doc(@"C:\d\7.txt", "rewritten during merge"));
            writer.AddOrUpdate(Doc(@"C:\new\x.txt", "brand new"));
        };

        Assert.True(writer.ForceMerge());
        storage.OnCreate = null;

        docs.Remove(@"C:\d\5.txt");
        var six = docs[@"C:\d\6.txt"];
        docs.Remove(six.Path);
        docs[@"C:\moved\6.txt"] = six with { Path = @"C:\moved\6.txt" };
        docs[@"C:\d\7.txt"] = Doc(@"C:\d\7.txt", "rewritten during merge");
        docs[@"C:\new\x.txt"] = Doc(@"C:\new\x.txt", "brand new");
        Assert.Equal(LogicalDump.Of(MemoryIndex.Build(docs.Values)), LogicalDump.Of(writer));
        Assert.True(fired);
    }

    [Fact]
    public void Merge_is_abandoned_when_a_committed_change_still_points_into_it()
    {
        var storage = new FakeIndexStorage();
        using var writer = IndexWriter.Open(storage, Options);
        var docs = Fill(writer, 12).ToDictionary(d => d.Path);
        var fired = false;
        storage.OnCreate = name =>
        {
            if (!fired && name.StartsWith("seg_", StringComparison.Ordinal))
            {
                fired = true;
                writer.Rename(@"C:\d\1.txt", @"C:\moved\1.txt", 10, T0);
                writer.Commit();
            }
        };

        Assert.False(writer.ForceMerge());
        storage.OnCreate = null;
        Assert.Equal(1, writer.GetStatistics().MergesAborted);

        var one = docs[@"C:\d\1.txt"];
        docs.Remove(one.Path);
        docs[@"C:\moved\1.txt"] = one with { Path = @"C:\moved\1.txt" };
        Assert.Equal(LogicalDump.Of(MemoryIndex.Build(docs.Values)), LogicalDump.Of(writer));
        Assert.True(writer.ForceMerge(), "The next attempt succeeds.");
        Assert.Equal(LogicalDump.Of(MemoryIndex.Build(docs.Values)), LogicalDump.Of(writer));
    }

    [Fact]
    public void Policy_levels_and_selection()
    {
        var policy = new MergePolicy { MergeFactor = 3, MinSegmentDocs = 10, ExpungeDeletesRatio = 0.5 };

        Assert.Equal(0, policy.LevelOf(10));
        Assert.Equal(1, policy.LevelOf(11));
        Assert.Equal(1, policy.LevelOf(29));
        Assert.Equal(2, policy.LevelOf(30));
        Assert.Null(policy.Select([(1, 5, 5), (2, 5, 5), (3, 50, 50)]));
        Assert.Equal([1L, 2, 4], policy.Select([(1, 5, 5), (2, 5, 5), (3, 50, 50), (4, 8, 8)]));
        Assert.Equal([3L], policy.Select([(1, 5, 5), (3, 50, 20)]));
    }
}
