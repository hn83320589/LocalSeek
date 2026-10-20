using System.Text;
using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.Extraction;
using LocalSeek.Indexing.Watching;
using LocalSeek.Tests.Fakes;
using static LocalSeek.Tests.Pipeline.EngineFixture;

namespace LocalSeek.Tests.Pipeline;

public class IndexingEngineTests
{
    private static FakeSourceFileSystem Files() => new FakeSourceFileSystem()
        .File($@"{Root}\a\報告.txt", "台灣大學 AI模型 第一版")
        .File($@"{Root}\a\notes.md", "apple banana")
        .File($@"{Root}\b\page.html", "<p>資料<b>分析</b></p>");

    [Fact]
    public async Task Initial_scan_indexes_everything_supported()
    {
        using var f = new EngineFixture(Files());

        await f.ScanAll();

        Assert.Equal([$@"{Root}\a\notes.md", $@"{Root}\a\報告.txt", $@"{Root}\b\page.html"], f.IndexedPaths());
        Assert.Equal(3, f.Engine.BodyAnalysisCount);
        Assert.Equal(f.Dump(), await f.RebuiltDump());
    }

    // ------------------------------------------------------------------ acceptance #12

    [Fact]
    public async Task Rename_event_updates_the_path_without_extracting_or_tokenizing()
    {
        using var f = new EngineFixture(Files());
        await f.ScanAll();
        var (extractions, analyses) = (f.Extractor.ExtractionCount, f.Engine.BodyAnalysisCount);

        f.FileSystem.Move($@"{Root}\a\報告.txt", $@"{Root}\a\期末報告.txt");
        f.Event(FileChangeKind.Renamed, $@"{Root}\a\期末報告.txt", $@"{Root}\a\報告.txt");
        await f.Settle();

        Assert.Equal(extractions, f.Extractor.ExtractionCount);
        Assert.Equal(analyses, f.Engine.BodyAnalysisCount);
        Assert.Contains($@"{Root}\a\期末報告.txt", f.IndexedPaths());
        Assert.DoesNotContain($@"{Root}\a\報告.txt", f.IndexedPaths());
        Assert.Equal(await f.RebuiltDump(), f.Dump());
    }

    [Fact]
    public async Task Move_across_folders_reported_as_delete_and_create_is_detected_by_hash()
    {
        using var f = new EngineFixture(Files());
        await f.ScanAll();
        var (extractions, analyses) = (f.Extractor.ExtractionCount, f.Engine.BodyAnalysisCount);

        f.FileSystem.Move($@"{Root}\a\notes.md", $@"{Root}\b\deep\notes.md");
        f.Event(FileChangeKind.Deleted, $@"{Root}\a\notes.md");
        f.Event(FileChangeKind.Created, $@"{Root}\b\deep\notes.md");
        await f.Settle();

        Assert.Equal(extractions, f.Extractor.ExtractionCount);
        Assert.Equal(analyses, f.Engine.BodyAnalysisCount);
        Assert.Contains($@"{Root}\b\deep\notes.md", f.IndexedPaths());
        Assert.Equal(await f.RebuiltDump(), f.Dump());
    }

    [Fact]
    public async Task Directory_rename_moves_all_files_without_extracting()
    {
        using var f = new EngineFixture(Files());
        await f.ScanAll();
        var (extractions, analyses) = (f.Extractor.ExtractionCount, f.Engine.BodyAnalysisCount);

        f.FileSystem.Move($@"{Root}\a", $@"{Root}\archive\2024");
        f.Event(FileChangeKind.Renamed, $@"{Root}\archive\2024", $@"{Root}\a");
        await f.Settle();

        Assert.Equal(extractions, f.Extractor.ExtractionCount);
        Assert.Equal(analyses, f.Engine.BodyAnalysisCount);
        Assert.Equal([$@"{Root}\archive\2024\notes.md", $@"{Root}\archive\2024\報告.txt", $@"{Root}\b\page.html"], f.IndexedPaths());
        Assert.Equal(await f.RebuiltDump(), f.Dump());
    }

    [Fact]
    public async Task Moves_found_by_a_difference_scan_are_not_extracted_either()
    {
        using var f = new EngineFixture(Files());
        await f.ScanAll();
        var (extractions, analyses) = (f.Extractor.ExtractionCount, f.Engine.BodyAnalysisCount);

        f.FileSystem.Move($@"{Root}\a", $@"{Root}\c");
        f.Engine.RequestScan(Root);
        await f.Engine.ProcessAsync();

        Assert.Equal(extractions, f.Extractor.ExtractionCount);
        Assert.Equal(analyses, f.Engine.BodyAnalysisCount);
        Assert.Equal(await f.RebuiltDump(), f.Dump());
    }

    [Fact]
    public async Task Touch_without_content_change_updates_metadata_only()
    {
        using var f = new EngineFixture(Files());
        await f.ScanAll();
        var analyses = f.Engine.BodyAnalysisCount;

        f.FileSystem.Write($@"{Root}\a\notes.md", "apple banana");
        f.Event(FileChangeKind.Changed, $@"{Root}\a\notes.md");
        await f.Settle();

        Assert.Equal(analyses, f.Engine.BodyAnalysisCount);
        Assert.True(f.Writer.TryGetEntry($@"{Root}\a\notes.md", out var entry));
        Assert.Equal(f.FileSystem.GetEntry($@"{Root}\a\notes.md")!.LastWriteUtc, entry.LastWriteUtc);
    }

    // ------------------------------------------------------------------ change handling

    [Fact]
    public async Task Modified_file_is_reindexed_and_deleted_file_disappears()
    {
        using var f = new EngineFixture(Files());
        await f.ScanAll();

        f.FileSystem.Write($@"{Root}\a\notes.md", "cherry");
        f.Event(FileChangeKind.Changed, $@"{Root}\a\notes.md");
        f.FileSystem.Delete($@"{Root}\b\page.html");
        f.Event(FileChangeKind.Deleted, $@"{Root}\b\page.html");
        await f.Settle();

        Assert.Equal([$@"{Root}\a\notes.md", $@"{Root}\a\報告.txt"], f.IndexedPaths());
        Assert.Equal(await f.RebuiltDump(), f.Dump());
    }

    [Fact]
    public async Task Changes_are_debounced_but_never_delayed_beyond_the_maximum()
    {
        using var f = new EngineFixture(Files());
        await f.ScanAll();
        var path = $@"{Root}\a\notes.md";

        f.Event(FileChangeKind.Changed, path);
        f.Time.Advance(TimeSpan.FromMilliseconds(400));
        await f.Engine.ProcessAsync();
        Assert.Equal(1, f.Engine.PendingCount);

        // Keep "saving" every 400 ms: the quiet period never elapses, the 1.5 s cap still triggers.
        for (var i = 0; i < 3; i++)
        {
            f.Event(FileChangeKind.Changed, path);
            f.Time.Advance(TimeSpan.FromMilliseconds(400));
            await f.Engine.ProcessAsync();
        }

        Assert.Equal(0, f.Engine.PendingCount);
        Assert.Null(f.Engine.NextDue);
    }

    [Fact]
    public async Task Deleting_a_directory_removes_everything_below_it()
    {
        using var f = new EngineFixture(Files());
        await f.ScanAll();

        f.FileSystem.Delete($@"{Root}\a");
        f.Event(FileChangeKind.Deleted, $@"{Root}\a");
        await f.Settle();

        Assert.Equal([$@"{Root}\b\page.html"], f.IndexedPaths());
    }

    [Fact]
    public async Task Locked_file_is_retried_with_increasing_delays_then_indexed()
    {
        using var f = new EngineFixture(Files());
        var path = $@"{Root}\a\notes.md";
        f.FileSystem.Lock(path, reads: 2);

        await f.ScanAll();
        Assert.DoesNotContain(path, f.IndexedPaths());
        Assert.Equal(DiagnosticKind.LockedRetryLater, Entry(f, path).Kind);

        await f.Settle(TimeSpan.FromMilliseconds(900));
        Assert.DoesNotContain(path, f.IndexedPaths());
        await f.Settle(TimeSpan.FromMilliseconds(100));
        Assert.DoesNotContain(path, f.IndexedPaths());
        Assert.Contains("第 2 次", Entry(f, path).Message, StringComparison.Ordinal);

        await f.Settle(TimeSpan.FromSeconds(2));
        Assert.Contains(path, f.IndexedPaths());
        Assert.False(f.Diagnostics.TryGet(path, out _));
    }

    [Fact]
    public async Task Permanently_locked_file_is_given_up_after_three_retries()
    {
        using var f = new EngineFixture(Files());
        var path = $@"{Root}\a\notes.md";
        f.FileSystem.Lock(path);

        await f.ScanAll();
        foreach (var wait in new[] { 1, 2, 4 })
        {
            await f.Settle(TimeSpan.FromSeconds(wait));
        }

        Assert.Equal(DiagnosticKind.LockedGaveUp, Entry(f, path).Kind);
        Assert.Equal(0, f.Engine.PendingCount);
        Assert.Equal(2, f.IndexedPaths().Count);
    }

    [Fact]
    public async Task File_changed_while_being_read_is_read_again_later()
    {
        using var f = new EngineFixture(Files());
        f.FileSystem.ChangeDuringNextRead($@"{Root}\a\notes.md", "final content");

        await f.ScanAll();
        Assert.DoesNotContain($@"{Root}\a\notes.md", f.IndexedPaths());

        await f.Settle();
        Assert.Contains($@"{Root}\a\notes.md", f.IndexedPaths());
        Assert.Equal(await f.RebuiltDump(), f.Dump());
    }

    [Fact]
    public async Task Skipped_files_are_diagnosed_and_not_indexed_even_by_name()
    {
        var fs = Files()
            .File($@"{Root}\bin.txt", new byte[100])
            .File($@"{Root}\big.txt", new string('x', 200))
            .File($@"{Root}\weird.txt", [0x41, 0xFF, 0xFE, 0x80]);
        using var f = new EngineFixture(fs, new ExtractionOptions { MaxFileBytes = 150 });

        await f.ScanAll();

        Assert.Equal(DiagnosticKind.Binary, Entry(f, $@"{Root}\bin.txt").Kind);
        Assert.Equal(DiagnosticKind.TooLarge, Entry(f, $@"{Root}\big.txt").Kind);
        Assert.Equal(DiagnosticKind.UnknownEncoding, Entry(f, $@"{Root}\weird.txt").Kind);
        Assert.Equal(3, f.IndexedPaths().Count);

        f.FileSystem.Write($@"{Root}\bin.txt", "now text");
        f.Event(FileChangeKind.Changed, $@"{Root}\bin.txt");
        await f.Settle();
        Assert.Contains($@"{Root}\bin.txt", f.IndexedPaths());
        Assert.False(f.Diagnostics.TryGet($@"{Root}\bin.txt", out _));
    }

    [Fact]
    public async Task Previously_indexed_file_that_becomes_too_large_is_removed()
    {
        using var f = new EngineFixture(Files(), new ExtractionOptions { MaxFileBytes = 100 });
        await f.ScanAll();

        f.FileSystem.Write($@"{Root}\a\notes.md", new string('z', 101));
        f.Event(FileChangeKind.Changed, $@"{Root}\a\notes.md");
        await f.Settle();

        Assert.DoesNotContain($@"{Root}\a\notes.md", f.IndexedPaths());
        Assert.Equal(DiagnosticKind.TooLarge, Entry(f, $@"{Root}\a\notes.md").Kind);
    }

    [Fact]
    public async Task Excluded_and_hidden_paths_from_events_are_ignored()
    {
        using var f = new EngineFixture(Files(), exclusions: ["**/tmp/"]);
        await f.ScanAll();

        f.FileSystem.File($@"{Root}\tmp\x.txt", "x");
        f.FileSystem.Directory($@"{Root}\.cache", FileAttributes.Hidden);
        f.FileSystem.File($@"{Root}\.cache\y.txt", "y");
        f.FileSystem.File($@"{Root}\image.png", "png");
        f.Event(FileChangeKind.Created, $@"{Root}\tmp\x.txt");
        f.Event(FileChangeKind.Created, $@"{Root}\.cache\y.txt");
        f.Event(FileChangeKind.Created, $@"{Root}\image.png");
        await f.Settle();

        Assert.Equal(3, f.IndexedPaths().Count);
    }

    [Fact]
    public async Task Newly_created_directory_with_files_is_crawled()
    {
        using var f = new EngineFixture(Files());
        await f.ScanAll();

        f.FileSystem.File($@"{Root}\new\x\1.txt", "one").File($@"{Root}\new\2.md", "two");
        f.Event(FileChangeKind.Created, $@"{Root}\new");
        await f.Settle();

        Assert.Contains($@"{Root}\new\x\1.txt", f.IndexedPaths());
        Assert.Contains($@"{Root}\new\2.md", f.IndexedPaths());
    }

    [Fact]
    public async Task Overflow_triggers_a_difference_scan_that_catches_up()
    {
        using var f = new EngineFixture(Files());
        await f.ScanAll();

        f.FileSystem.Write($@"{Root}\a\notes.md", "silently changed");
        f.FileSystem.Delete($@"{Root}\b\page.html");
        f.FileSystem.File($@"{Root}\c\new.txt", Encoding.UTF8.GetBytes("silently added"));
        f.Engine.RequestScan(Root);
        await f.Engine.ProcessAsync();

        Assert.Equal(await f.RebuiltDump(), f.Dump());
    }

    private static DiagnosticEntry Entry(EngineFixture f, string path) =>
        f.Diagnostics.TryGet(path, out var entry) ? entry : throw new Xunit.Sdk.XunitException($"No diagnostic for {path}");
}
