
using LocalSeek.Core.Index;
using LocalSeek.Tests.Fakes;
using static LocalSeek.Tests.Index.IndexWriterTests;

namespace LocalSeek.Tests.Index;

/// <summary>Acceptance #19: damaged or mismatched index files are reported and only the affected part is lost.</summary>
public class CorruptionTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Two committed segments: "first" (a, b) and "second" (c). Returns the segment file names in order.</summary>
    private static (FakeIndexStorage Storage, string First, string Second) TwoSegments()
    {
        var storage = new FakeIndexStorage();
        using (var writer = IndexWriter.Open(storage))
        {
            writer.AddOrUpdate(Doc(@"C:\a.txt", "apple"));
            writer.AddOrUpdate(Doc(@"C:\b.txt", "banana"));
            writer.Commit();
            writer.AddOrUpdate(Doc(@"C:\c.txt", "cherry"));
            writer.Commit();
        }

        var segments = storage.Files("seg_", ".lss");
        return (storage, segments[0], segments[1]);
    }

    private static string Paths(IndexWriter writer)
    {
        using var snapshot = writer.AcquireSnapshot();
        return string.Join(",", Enumerable.Range(0, snapshot.DocCount).Select(d => snapshot.GetMetadata(d).Path).Order());
    }

    [Fact]
    public void Segment_version_mismatch_drops_only_that_segment()
    {
        var (storage, first, _) = TwoSegments();
        storage.FlipByte(first, 8);

        using var writer = IndexWriter.Open(storage);

        var issue = Assert.Single(writer.OpenReport.Issues);
        Assert.Equal(IndexIssueKind.FormatVersionMismatch, issue.Kind);
        Assert.Equal(first, issue.File);
        Assert.Equal(2, issue.AffectedDocuments);
        Assert.Contains("版本", issue.Message, StringComparison.Ordinal);
        Assert.Equal(@"C:\c.txt", Paths(writer));
    }

    [Fact]
    public void Segment_checksum_mismatch_drops_only_that_segment()
    {
        var (storage, _, second) = TwoSegments();
        storage.FlipByte(second, 40);

        using var writer = IndexWriter.Open(storage);

        Assert.Equal(IndexIssueKind.SegmentCorrupt, Assert.Single(writer.OpenReport.Issues).Kind);
        Assert.Equal(@"C:\a.txt,C:\b.txt", Paths(writer));
    }

    [Fact]
    public void Truncated_segment_is_detected()
    {
        var (storage, first, _) = TwoSegments();
        storage.ReplaceContent(first, storage.ReadAll(first)[..^10]);

        using var writer = IndexWriter.Open(storage);

        Assert.Equal(IndexIssueKind.SegmentCorrupt, Assert.Single(writer.OpenReport.Issues).Kind);
        Assert.Equal(@"C:\c.txt", Paths(writer));
    }

    [Fact]
    public void Missing_segment_file_is_reported()
    {
        var (storage, first, _) = TwoSegments();
        storage.Remove(first);

        using var writer = IndexWriter.Open(storage);

        Assert.Equal(IndexIssueKind.SegmentMissing, Assert.Single(writer.OpenReport.Issues).Kind);
        Assert.Equal(@"C:\c.txt", Paths(writer));
    }

    [Fact]
    public void Repair_is_committed_so_the_next_open_is_clean()
    {
        var (storage, first, _) = TwoSegments();
        storage.FlipByte(first, 40);
        using (var writer = IndexWriter.Open(storage))
        {
            Assert.True(writer.OpenReport.HasIssues);
        }

        using var reopened = IndexWriter.Open(storage);
        Assert.False(reopened.OpenReport.HasIssues);
        Assert.Equal(@"C:\c.txt", Paths(reopened));
        Assert.False(storage.Exists(first));
    }

    [Fact]
    public void Postings_damage_is_found_by_full_verification()
    {
        var (storage, first, _) = TwoSegments();
        // Footer = 5 × (offset i64, length i64, crc u32) + headerCrc + footerCrc + magic; section 2 is postings.
        var bytes = storage.ReadAll(first);
        var footer = bytes.Length - ((5 * 20) + 16);
        var postingsOffset = BitConverter.ToInt64(bytes, footer + (2 * 20));
        storage.FlipByte(first, (int)postingsOffset + 1);

        using var writer = IndexWriter.Open(storage);
        Assert.False(writer.OpenReport.HasIssues, "Large sections are verified lazily.");

        var issues = writer.VerifySegments();

        Assert.Equal(first, Assert.Single(issues).File);
        Assert.Equal(@"C:\c.txt", Paths(writer));
    }

    [Fact]
    public void Corrupt_latest_manifest_falls_back_to_previous_commit()
    {
        var (storage, _, _) = TwoSegments();
        var latest = storage.Files("manifest_", ".lsm")[^1];
        storage.FlipByte(latest, 3);

        using var writer = IndexWriter.Open(storage);

        Assert.Equal(IndexIssueKind.ManifestUnreadable, Assert.Single(writer.OpenReport.Issues).Kind);
        Assert.Equal(@"C:\a.txt,C:\b.txt", Paths(writer));
    }

    [Fact]
    public void All_manifests_unreadable_requires_full_rebuild_without_crashing()
    {
        var (storage, _, _) = TwoSegments();
        foreach (var manifest in storage.Files("manifest_", ".lsm"))
        {
            storage.ReplaceContent(manifest, [1, 2, 3]);
        }

        using var writer = IndexWriter.Open(storage);

        Assert.True(writer.OpenReport.FullRebuildRequired);
        Assert.Equal("", Paths(writer));
        writer.AddOrUpdate(Doc(@"C:\new.txt", "fresh"));
        writer.Commit();
        Assert.Equal(@"C:\new.txt", Paths(writer));
    }

    [Fact]
    public void Manifest_version_mismatch_is_reported()
    {
        var storage = new FakeIndexStorage();
        using (var writer = IndexWriter.Open(storage))
        {
            writer.AddOrUpdate(Doc(@"C:\a.txt", "apple"));
            writer.Commit();
        }

        var name = Assert.Single(storage.Files("manifest_", ".lsm"));
        var manifest = new Manifest(99, 5, 10, []);
        var other = new FakeIndexStorage();
        manifest.Write(other);
        storage.ReplaceContent(name, other.ReadAll(IndexFileNames.Manifest(5)));

        using var reopened = IndexWriter.Open(storage);

        Assert.Contains(reopened.OpenReport.Issues, i => i.Kind == IndexIssueKind.FormatVersionMismatch);
        Assert.True(reopened.OpenReport.FullRebuildRequired);
    }

    [Fact]
    public void Corrupt_deletion_file_drops_its_segment()
    {
        var storage = new FakeIndexStorage();
        using (var writer = IndexWriter.Open(storage))
        {
            writer.AddOrUpdate(Doc(@"C:\a.txt", "apple"));
            writer.AddOrUpdate(Doc(@"C:\b.txt", "banana"));
            writer.Commit();
            writer.Delete(@"C:\a.txt");
            writer.AddOrUpdate(Doc(@"C:\c.txt", "cherry"));
            writer.Commit();
        }

        var deletions = Assert.Single(storage.Files("seg_", ".lsd"));
        storage.FlipByte(deletions, 20);

        using var reopened = IndexWriter.Open(storage);

        Assert.Equal(IndexIssueKind.DeletionsCorrupt, Assert.Single(reopened.OpenReport.Issues).Kind);
        Assert.Equal(@"C:\c.txt", Paths(reopened));
    }

    [Fact]
    public void Documents_renamed_onto_content_of_a_damaged_segment_are_dropped_too()
    {
        var storage = new FakeIndexStorage();
        using (var writer = IndexWriter.Open(storage))
        {
            writer.AddOrUpdate(Doc(@"C:\a.txt", "apple"));
            writer.Commit();
            writer.Rename(@"C:\a.txt", @"C:\moved.txt", 10, T0);
            writer.AddOrUpdate(Doc(@"C:\z.txt", "zebra"));
            writer.Commit();
        }

        storage.FlipByte(storage.Files("seg_", ".lss")[0], 40);

        using var reopened = IndexWriter.Open(storage);

        Assert.Contains(reopened.OpenReport.Issues, i => i.Kind == IndexIssueKind.SegmentCorrupt);
        Assert.Contains(reopened.OpenReport.Issues, i => i.Kind == IndexIssueKind.DanglingDocuments && i.AffectedDocuments == 1);
        Assert.Equal(@"C:\z.txt", Paths(reopened));
    }

    [Fact]
    public void Orphan_and_temporary_files_are_removed_on_open()
    {
        var (storage, _, _) = TwoSegments();
        storage.ReplaceContent("seg_00000099.lss", [1, 2, 3]);
        storage.ReplaceContent("manifest_0000000099.lsm.tmp", [1]);

        using var writer = IndexWriter.Open(storage);

        Assert.False(writer.OpenReport.HasIssues);
        Assert.False(storage.Exists("seg_00000099.lss"));
        Assert.False(storage.Exists("manifest_0000000099.lsm.tmp"));
        Assert.Equal(@"C:\a.txt,C:\b.txt,C:\c.txt", Paths(writer));
    }
}
