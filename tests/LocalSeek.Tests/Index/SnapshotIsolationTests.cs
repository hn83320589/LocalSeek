using LocalSeek.Core.Index;
using LocalSeek.Tests.Fakes;
using static LocalSeek.Tests.Index.IndexWriterTests;

namespace LocalSeek.Tests.Index;

/// <summary>Acceptance #14.</summary>
public class SnapshotIsolationTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Open_snapshot_is_unaffected_by_later_commits()
    {
        using var writer = IndexWriter.Open(new FakeIndexStorage(), new IndexWriterOptions { RamBufferBytes = 500 });
        for (var i = 0; i < 20; i++)
        {
            writer.AddOrUpdate(Doc($@"C:\d\{i}.txt", $"apple 台灣大學 n{i}"));
        }

        writer.Commit();
        using var snapshot = writer.AcquireSnapshot();
        var before = LogicalDump.Of(snapshot);

        writer.Delete(@"C:\d\3.txt");
        writer.Rename(@"C:\d\4.txt", @"C:\e\4.txt", 10, T0);
        writer.AddOrUpdate(Doc(@"C:\d\5.txt", "changed"));
        writer.AddOrUpdate(Doc(@"C:\d\new.txt", "banana"));
        writer.Commit();
        for (var i = 0; i < 20; i++)
        {
            writer.Delete($@"C:\d\{i}.txt");
        }

        writer.Commit();

        Assert.Equal(before, LogicalDump.Of(snapshot));
        Assert.NotEqual(before, LogicalDump.Of(writer));
    }

    [Fact]
    public void Superseded_files_are_deleted_only_after_the_last_old_reader_releases()
    {
        var storage = new FakeIndexStorage();
        using var writer = IndexWriter.Open(storage);
        writer.AddOrUpdate(Doc(@"C:\a.txt", "apple"));
        writer.Commit();
        var oldSegment = Assert.Single(storage.Files("seg_", ".lss"));

        var reader1 = writer.AcquireSnapshot();
        var reader2 = writer.AcquireSnapshot();
        writer.Delete(@"C:\a.txt");
        writer.Commit();
        writer.AddOrUpdate(Doc(@"C:\b.txt", "b"));
        writer.Commit();
        writer.AddOrUpdate(Doc(@"C:\c.txt", "c"));
        writer.Commit();

        Assert.True(storage.Exists(oldSegment));
        reader1.Dispose();
        Assert.True(storage.Exists(oldSegment));
        Assert.Equal(1, reader2.DocCount);
        reader2.Dispose();
        Assert.False(storage.Exists(oldSegment));
    }

    [Fact]
    public async Task Snapshot_keeps_working_while_writer_continues_on_other_threads()
    {
        using var writer = IndexWriter.Open(new FakeIndexStorage(), new IndexWriterOptions { RamBufferBytes = 800 });
        for (var i = 0; i < 30; i++)
        {
            writer.AddOrUpdate(Doc($@"C:\d\{i}.txt", $"apple n{i}"));
        }

        writer.Commit();
        using var snapshot = writer.AcquireSnapshot();
        var expected = LogicalDump.Of(snapshot);

        var mutator = Task.Run(() =>
        {
            for (var round = 0; round < 10; round++)
            {
                writer.Delete($@"C:\d\{round}.txt");
                writer.AddOrUpdate(Doc($@"C:\x\{round}.txt", "apple more"));
                writer.Commit();
            }
        });
        while (!mutator.IsCompleted)
        {
            Assert.Equal(expected, LogicalDump.Of(snapshot));
        }

        await mutator;
        Assert.Equal(expected, LogicalDump.Of(snapshot));
    }

    [Fact]
    public void Releasing_a_snapshot_twice_is_an_error()
    {
        using var writer = IndexWriter.Open(new FakeIndexStorage());
        var snapshot = writer.AcquireSnapshot();
        snapshot.Dispose();
        writer.AddOrUpdate(Doc(@"C:\a.txt", "a"));
        writer.Commit();

        Assert.Throws<ObjectDisposedException>(snapshot.Dispose);
    }
}
