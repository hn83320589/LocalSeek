using LocalSeek.Core.Query;
using LocalSeek.Indexing.Extraction;
using LocalSeek.Indexing.Preview;
using LocalSeek.Tests.Fakes;

namespace LocalSeek.Tests.Pipeline;

public class DocumentPreviewerTests
{
    private static BoundNode Q(string query) => QueryBinder.Bind(QueryParser.Parse(query));

    private readonly FakeSourceFileSystem _fs = new FakeSourceFileSystem()
        .File(@"C:\r\台灣報告.html", "<p>這是<b>台灣</b>的報告</p><script>台灣</script>")
        .File(@"C:\r\bin.txt", new byte[50]);

    private DocumentPreview Preview(string path, string query) =>
        new DocumentPreviewer(_fs, new ContentExtractor(new ExtractionOptions())).Create(path, Q(query));

    [Fact]
    public void Preview_highlights_extracted_body_and_file_name()
    {
        var preview = Preview(@"C:\r\台灣報告.html", "台灣");

        var snippet = Assert.Single(preview.Snippets);
        var highlight = Assert.Single(snippet.Highlights);
        Assert.Equal("台灣", snippet.Text.Substring(highlight.Start, highlight.Length));
        Assert.Equal((0, 2), (preview.NameHighlights[0].Start, preview.NameHighlights[0].Length));
        Assert.Null(preview.Message);
    }

    [Fact]
    public void Missing_or_unreadable_files_explain_why()
    {
        Assert.Equal("檔案已不存在", Preview(@"C:\r\gone.txt", "x").Message);
        Assert.Contains("NUL", Preview(@"C:\r\bin.txt", "x").Message, StringComparison.Ordinal);
        _fs.Lock(@"C:\r\台灣報告.html");
        Assert.Contains("鎖定", Preview(@"C:\r\台灣報告.html", "台灣").Message, StringComparison.Ordinal);
    }
}
