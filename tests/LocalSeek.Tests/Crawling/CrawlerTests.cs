using LocalSeek.Indexing.Crawling;
using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.FileSystem;
using LocalSeek.Tests.Fakes;

namespace LocalSeek.Tests.Crawling;

/// <summary>Acceptance #13 against the fake file system.</summary>
public class CrawlerTests
{
    private readonly DiagnosticsLog _diagnostics = new(TimeProvider.System);

    private List<string> Crawl(ISourceFileSystem fs, string root = @"C:\root", ExclusionRules? rules = null, CrawlOptions? options = null) =>
        new Crawler(fs, _diagnostics).Crawl(root, rules ?? ExclusionRules.None, options ?? new CrawlOptions())
            .Select(e => e.FullPath).ToList();

    private DiagnosticKind KindOf(string path) =>
        Assert.IsType<DiagnosticEntry>(_diagnostics.TryGet(path, out var entry) ? entry : null).Kind;

    [Fact]
    public void Finds_supported_files_in_name_order_and_ignores_other_types()
    {
        var fs = new FakeSourceFileSystem()
            .File(@"C:\root\b.txt", "b").File(@"C:\root\a.md", "a").File(@"C:\root\sub\c.docx", "c")
            .File(@"C:\root\image.png", "x").File(@"C:\root\sub\deeper\d.HTML", "d");

        Assert.Equal([@"C:\root\a.md", @"C:\root\b.txt", @"C:\root\sub\c.docx", @"C:\root\sub\deeper\d.HTML"], Crawl(fs));
        Assert.Equal(0, _diagnostics.Count);
    }

    [Fact]
    public void Junction_cycle_back_to_an_ancestor_terminates()
    {
        var fs = new FakeSourceFileSystem()
            .File(@"C:\root\a.txt", "a")
            .File(@"C:\root\sub\b.txt", "b")
            .Junction(@"C:\root\sub\loop", @"C:\root");

        Assert.Equal([@"C:\root\a.txt", @"C:\root\sub\b.txt"], Crawl(fs));
        Assert.Equal(DiagnosticKind.LoopDetected, KindOf(@"C:\root\sub\loop"));
    }

    [Fact]
    public void Mutual_junctions_outside_the_root_terminate()
    {
        var fs = new FakeSourceFileSystem()
            .File(@"C:\root\a.txt", "a")
            .Junction(@"C:\root\x", @"D:\x")
            .File(@"D:\x\in-x.txt", "x")
            .Junction(@"D:\x\to-y", @"D:\y")
            .File(@"D:\y\in-y.txt", "y")
            .Junction(@"D:\y\to-x", @"D:\x");

        Assert.Equal([@"C:\root\a.txt", @"C:\root\x\in-x.txt", @"C:\root\x\to-y\in-y.txt"], Crawl(fs));
        Assert.Equal(DiagnosticKind.LoopDetected, KindOf(@"C:\root\x\to-y\to-x"));
    }

    [Fact]
    public void Two_links_to_the_same_directory_index_it_once()
    {
        var fs = new FakeSourceFileSystem()
            .Junction(@"C:\root\first", @"D:\shared")
            .Junction(@"C:\root\second", @"D:\shared")
            .File(@"D:\shared\s.txt", "s");

        Assert.Equal([@"C:\root\first\s.txt"], Crawl(fs));
        Assert.Equal(DiagnosticKind.LoopDetected, KindOf(@"C:\root\second"));
    }

    [Fact]
    public void Broken_link_is_reported()
    {
        var fs = new FakeSourceFileSystem().File(@"C:\root\a.txt", "a").Junction(@"C:\root\gone", @"E:\missing");

        Assert.Equal([@"C:\root\a.txt"], Crawl(fs));
        Assert.Equal(DiagnosticKind.BrokenLink, KindOf(@"C:\root\gone"));
    }

    [Fact]
    public void Links_can_be_ignored_by_setting()
    {
        var fs = new FakeSourceFileSystem().Junction(@"C:\root\link", @"D:\x").File(@"D:\x\a.txt", "a");

        Assert.Empty(Crawl(fs, options: new CrawlOptions(FollowReparsePoints: false)));
        Assert.Equal(DiagnosticKind.ReparsePointSkipped, KindOf(@"C:\root\link"));
    }

    [Fact]
    public void Paths_longer_than_260_characters_are_crawled()
    {
        var deep = @"C:\root\" + string.Join('\\', Enumerable.Repeat("很長的資料夾名稱abcdefghij", 20));
        var file = deep + @"\深.txt";
        var fs = new FakeSourceFileSystem().File(file, "deep");

        Assert.True(file.Length > 260);
        Assert.Equal([file], Crawl(fs));
    }

    [Fact]
    public void Access_denied_folder_is_reported_and_siblings_continue()
    {
        var fs = new FakeSourceFileSystem()
            .File(@"C:\root\a\secret.txt", "s")
            .File(@"C:\root\b\open.txt", "o");
        fs.Deny(@"C:\root\a");

        Assert.Equal([@"C:\root\b\open.txt"], Crawl(fs));
        Assert.Equal(DiagnosticKind.AccessDenied, KindOf(@"C:\root\a"));
    }

    [Fact]
    public void Hidden_and_system_entries_are_skipped_with_diagnostics()
    {
        var fs = new FakeSourceFileSystem()
            .File(@"C:\root\visible.txt", "v")
            .File(@"C:\root\~$draft.docx", "h", FileAttributes.Hidden)
            .File(@"C:\root\sys.txt", "s", FileAttributes.System)
            .Directory(@"C:\root\.hidden", FileAttributes.Hidden)
            .File(@"C:\root\.hidden\inner.txt", "i");

        Assert.Equal([@"C:\root\visible.txt"], Crawl(fs));
        Assert.Equal(DiagnosticKind.HiddenOrSystem, KindOf(@"C:\root\~$draft.docx"));
        Assert.Equal(DiagnosticKind.HiddenOrSystem, KindOf(@"C:\root\sys.txt"));
        Assert.Equal(DiagnosticKind.HiddenOrSystem, KindOf(@"C:\root\.hidden"));
        Assert.Equal(3, _diagnostics.Count);
    }

    [Fact]
    public void Hidden_entries_are_included_when_the_setting_is_off()
    {
        var fs = new FakeSourceFileSystem().File(@"C:\root\h.txt", "h", FileAttributes.Hidden);

        Assert.Equal([@"C:\root\h.txt"], Crawl(fs, options: new CrawlOptions(SkipHiddenAndSystem: false)));
    }

    [Fact]
    public void Exclusion_rules_apply_to_files_and_prune_directories()
    {
        var fs = new FakeSourceFileSystem()
            .File(@"C:\root\keep.txt", "k")
            .File(@"C:\root\drop.tmp.txt", "d")
            .File(@"C:\root\build\out.txt", "o")
            .File(@"C:\root\src\build\nested.txt", "n")
            .File(@"C:\root\src\code.md", "c");
        var statistics = new CrawlStatistics();

        var found = new Crawler(fs, _diagnostics)
            .Crawl(@"C:\root", new ExclusionRules(["**/build/", "*.tmp.txt"]), new CrawlOptions(), statistics).Select(e => e.FullPath).ToList();

        Assert.Equal([@"C:\root\keep.txt", @"C:\root\src\code.md"], found);
        Assert.Equal(3, statistics.Excluded);
        Assert.Equal(2, statistics.Files);
    }

    [Fact]
    public void Missing_root_is_reported_without_throwing()
    {
        Assert.Empty(Crawl(new FakeSourceFileSystem(), @"Z:\nowhere"));
        Assert.Equal(DiagnosticKind.RootUnavailable, KindOf(@"Z:\nowhere"));
    }

    [Fact]
    public void Root_unavailable_is_cleared_when_the_root_returns_but_other_root_notices_stay()
    {
        var fs = new FakeSourceFileSystem();
        Crawl(fs, @"C:\late");
        fs.File(@"C:\late\a.txt", "a");

        Assert.Equal([@"C:\late\a.txt"], Crawl(fs, @"C:\late"));
        Assert.False(_diagnostics.TryGet(@"C:\late", out _));

        _diagnostics.Record(@"C:\late", DiagnosticKind.WatcherFallback, "periodic");
        Crawl(fs, @"C:\late");
        Assert.Equal(DiagnosticKind.WatcherFallback, KindOf(@"C:\late"));
    }

    [Fact]
    public void Reader_classifies_locked_changed_missing_and_denied_files()
    {
        var fs = new FakeSourceFileSystem()
            .File(@"C:\r\locked.txt", "l").File(@"C:\r\changing.txt", "v1").File(@"C:\r\denied.txt", "d").File(@"C:\r\ok.txt", "ok");
        fs.Lock(@"C:\r\locked.txt", reads: 1);
        fs.ChangeDuringNextRead(@"C:\r\changing.txt", "version two");
        fs.Deny(@"C:\r\denied.txt");

        Assert.IsType<ReadOutcome.Locked>(SourceFileReader.Read(fs, @"C:\r\locked.txt"));
        Assert.IsType<ReadOutcome.Success>(SourceFileReader.Read(fs, @"C:\r\locked.txt"));
        Assert.IsType<ReadOutcome.Changed>(SourceFileReader.Read(fs, @"C:\r\changing.txt"));
        Assert.IsType<ReadOutcome.Success>(SourceFileReader.Read(fs, @"C:\r\changing.txt"));
        Assert.IsType<ReadOutcome.AccessDenied>(SourceFileReader.Read(fs, @"C:\r\denied.txt"));
        Assert.IsType<ReadOutcome.Missing>(SourceFileReader.Read(fs, @"C:\r\gone.txt"));
        var ok = Assert.IsType<ReadOutcome.Success>(SourceFileReader.Read(fs, @"C:\r\ok.txt"));
        Assert.Equal("ok"u8.ToArray(), ok.Bytes);
    }

    [Fact]
    public void Diagnostics_log_tracks_one_current_entry_per_path()
    {
        _diagnostics.Record(@"C:\a\x.txt", DiagnosticKind.LockedRetryLater, "1");
        _diagnostics.Record(@"C:\A\X.TXT", DiagnosticKind.Binary, "2");
        _diagnostics.Record(@"C:\a\sub\y.txt", DiagnosticKind.TooLarge, "3");
        _diagnostics.Record(@"C:\ab\z.txt", DiagnosticKind.TooLarge, "4");

        Assert.Equal(3, _diagnostics.Count);
        Assert.Equal(DiagnosticKind.Binary, KindOf(@"C:\a\x.txt"));
        _diagnostics.ClearUnder(@"C:\a");
        Assert.Equal([@"C:\ab\z.txt"], _diagnostics.Entries().Select(e => e.Path));
        Assert.Equal(1, _diagnostics.CountsByKind()[DiagnosticKind.TooLarge]);
    }
}
