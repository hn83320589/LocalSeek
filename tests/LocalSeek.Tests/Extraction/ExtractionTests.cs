using System.IO.Compression;
using System.Text;
using LocalSeek.Indexing.Extraction;

namespace LocalSeek.Tests.Extraction;

public class ExtractionTests
{
    static ExtractionTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly ContentExtractor Extractor = new(new ExtractionOptions());

    private static Encoding Big5 => Encoding.GetEncoding(950);

    private static ExtractionResult Extract(string fileName, byte[] bytes) => Extractor.Extract(fileName, bytes);

    private static string Text(ExtractionResult result) =>
        Assert.IsType<ExtractionResult.Success>(result).Text;

    private static SkipReason Skipped(ExtractionResult result) =>
        Assert.IsType<ExtractionResult.Skipped>(result).Reason;

    private const string Sample = "台灣大學 AI模型 café ✓ 𠮷";

    [Fact]
    public void Utf8_without_bom()
    {
        var result = Extract("a.txt", Encoding.UTF8.GetBytes(Sample));

        Assert.Equal(Sample, Text(result));
        Assert.Equal("UTF-8", ((ExtractionResult.Success)result).Encoding);
    }

    [Fact]
    public void Utf8_with_bom_strips_the_bom()
    {
        var result = Extract("a.md", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Sample)]);

        Assert.Equal(Sample, Text(result));
        Assert.Equal("UTF-8 BOM", ((ExtractionResult.Success)result).Encoding);
    }

    [Fact]
    public void Utf16_little_endian_with_bom()
    {
        Assert.Equal(Sample, Text(Extract("a.log", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Sample)])));
    }

    [Fact]
    public void Utf16_big_endian_with_bom()
    {
        Assert.Equal(Sample, Text(Extract("a.json", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Sample)])));
    }

    [Fact]
    public void Big5_legacy_file()
    {
        const string text = "臺灣資料分析報告，民國八十年。ABC 123";
        var result = Extract("old.txt", Big5.GetBytes(text));

        Assert.Equal(text, Text(result));
        Assert.Equal("Big5", ((ExtractionResult.Success)result).Encoding);
    }

    [Fact]
    public void Big5_csv()
    {
        const string text = "姓名,電話\r\n王小明,02-1234\r\n";

        Assert.Equal(text, Text(Extract("客戶.csv", Big5.GetBytes(text))));
    }

    [Fact]
    public void Utf8_csv_and_xml_are_plain_text()
    {
        Assert.Equal("a,b\n1,2", Text(Extract("x.csv", Encoding.UTF8.GetBytes("a,b\n1,2"))));
        Assert.Equal("<r><v>台</v></r>", Text(Extract("x.xml", Encoding.UTF8.GetBytes("<r><v>台</v></r>"))));
    }

    [Fact]
    public void Empty_file_is_empty_text()
    {
        Assert.Equal("", Text(Extract("empty.txt", [])));
    }

    [Fact]
    public void Many_nul_bytes_mean_binary()
    {
        var bytes = new byte[1000];
        Encoding.ASCII.GetBytes("hello").CopyTo(bytes, 0);

        Assert.Equal(SkipReason.Binary, Skipped(Extract("data.txt", bytes)));
    }

    [Fact]
    public void Utf16_without_bom_is_treated_as_binary()
    {
        Assert.Equal(SkipReason.Binary, Skipped(Extract("u16.txt", Encoding.Unicode.GetBytes("hello world, this is text"))));
    }

    [Fact]
    public void Isolated_nul_below_threshold_is_not_binary()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 500) + "\0" + new string('b', 500));

        Assert.IsType<ExtractionResult.Success>(Extract("mostly.txt", bytes));
    }

    [Fact]
    public void Bytes_valid_in_neither_utf8_nor_big5_are_skipped()
    {
        var result = Extract("weird.txt", [0x41, 0xFF, 0xFF, 0x80, 0x42]);

        Assert.Equal(SkipReason.UnknownEncoding, Skipped(result));
        Assert.Contains("編碼", ((ExtractionResult.Skipped)result).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_over_the_size_limit_are_skipped()
    {
        var small = new ContentExtractor(new ExtractionOptions { MaxFileBytes = 10 });

        Assert.Equal(SkipReason.TooLarge, Skipped(small.Extract("big.txt", new byte[11])));
        Assert.IsType<ExtractionResult.Success>(small.Extract("ok.txt", Encoding.UTF8.GetBytes("0123456789")));
        Assert.True(small.IsTooLarge(11));
        Assert.False(small.IsTooLarge(10));
    }

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData("a.MD", true)]
    [InlineData("a.csv", true)]
    [InlineData("a.log", true)]
    [InlineData("a.json", true)]
    [InlineData("a.xml", true)]
    [InlineData("a.html", true)]
    [InlineData("a.htm", true)]
    [InlineData("a.docx", true)]
    [InlineData("a.doc", false)]
    [InlineData("a.pdf", false)]
    [InlineData("txt", false)]
    public void Supported_extensions(string fileName, bool supported)
    {
        Assert.Equal(supported, ContentExtractor.IsSupported(fileName));
    }

    [Fact]
    public void Unsupported_extension_is_skipped()
    {
        Assert.Equal(SkipReason.Unsupported, Skipped(Extract("a.pdf", [1, 2, 3])));
    }

    // ------------------------------------------------------------------ HTML

    [Fact]
    public void Html_skips_script_style_and_comments_and_decodes_entities()
    {
        const string html = """
            <!DOCTYPE html><html><head><title>標題</title>
            <style>body { color: red; }</style>
            <script type="text/javascript">var hidden = "secret";</script>
            </head><body><!-- comment secret -->
            <p>AT&amp;T &lt;tag&gt; &#20013;&#x6587; a&nbsp;b &copy;</p>
            <SCRIPT>alert('x')</SCRIPT>
            </body></html>
            """;

        var text = Text(Extract("page.html", Encoding.UTF8.GetBytes(html)));

        Assert.Contains("標題", text, StringComparison.Ordinal);
        Assert.Contains("AT&T <tag> 中文 a b ©", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_inline_tags_do_not_split_words_but_block_tags_do()
    {
        var text = Text(Extract("p.htm", Encoding.UTF8.GetBytes("<p>台<b>灣</b>大<span class='x'>學</span></p><div>next</div>line<br>break")));

        Assert.Contains("台灣大學", text, StringComparison.Ordinal);
        Assert.DoesNotContain("學next", text, StringComparison.Ordinal);
        Assert.DoesNotContain("linebreak", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_in_big5()
    {
        var text = Text(Extract("old.htm", Big5.GetBytes("<html><body><p>臺灣資料</p></body></html>")));

        Assert.Contains("臺灣資料", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_tolerates_unclosed_constructs()
    {
        Assert.Contains("text", Text(Extract("bad.html", Encoding.UTF8.GetBytes("text <p unclosed"))), StringComparison.Ordinal);
        Assert.DoesNotContain("x", Text(Extract("bad2.html", Encoding.UTF8.GetBytes("<script>x"))), StringComparison.Ordinal);
        Assert.Equal("a", Text(Extract("bad3.html", Encoding.UTF8.GetBytes("a<!-- never closed"))).Trim());
    }

    // ------------------------------------------------------------------ DOCX

    internal static byte[] Docx(string documentXml, string? headerXml = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }

            Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add("word/document.xml", documentXml);
            if (headerXml is not null)
            {
                Add("word/header1.xml", headerXml);
            }
        }

        return stream.ToArray();
    }

    internal const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Fact]
    public void Docx_paragraph_text_without_headers()
    {
        var document = $"""
            <w:document xmlns:w="{W}"><w:body>
              <w:p><w:r><w:t>第一段</w:t></w:r><w:r><w:t xml:space="preserve"> 台灣</w:t></w:r></w:p>
              <w:p><w:r><w:t>A</w:t><w:tab/><w:t>B</w:t><w:br/><w:t>C</w:t></w:r></w:p>
              <w:p><w:del><w:r><w:delText>刪除的字</w:delText></w:r></w:del><w:r><w:instrText>PAGE</w:instrText></w:r></w:p>
              <w:tbl><w:tr><w:tc><w:p><w:r><w:t>表格</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """;
        var header = $"""<w:hdr xmlns:w="{W}"><w:p><w:r><w:t>頁首文字</w:t></w:r></w:p></w:hdr>""";

        var text = Text(Extract("報告.docx", Docx(document, header)));

        Assert.Equal("第一段 台灣\nA\tB\nC\n\n表格\n", text);
    }

    [Fact]
    public void Corrupt_docx_is_skipped()
    {
        Assert.Equal(SkipReason.Corrupt, Skipped(Extract("broken.docx", [0x50, 0x4B, 0x03, 0x04, 1, 2, 3])));
    }

    [Fact]
    public void Docx_without_document_part_is_skipped()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("other.xml");
        }

        Assert.Equal(SkipReason.Corrupt, Skipped(Extract("empty.docx", stream.ToArray())));
    }

    [Fact]
    public void Docx_with_malformed_xml_is_skipped()
    {
        Assert.Equal(SkipReason.Corrupt, Skipped(Extract("bad.docx", Docx("<w:document><w:p>"))));
    }
}
