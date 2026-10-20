using System.IO.Compression;
using System.Text;
using System.Xml;

namespace LocalSeek.Indexing.Extraction;

/// <summary>
/// Paragraph text of word/document.xml: w:t runs, w:tab → tab, w:br/w:cr → newline, one newline per
/// paragraph. Headers, footers and footnotes live in other parts and are therefore excluded; deleted
/// text (w:delText) and field codes (w:instrText) are different elements and are skipped.
/// </summary>
internal static class DocxTextExtractor
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static ExtractionResult Extract(byte[] bytes, ExtractionOptions options)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null)
            {
                return new ExtractionResult.Skipped(SkipReason.Corrupt, "docx 缺少 word/document.xml");
            }

            if (entry.Length > options.MaxDocxPartBytes)
            {
                return new ExtractionResult.Skipped(SkipReason.TooLarge, "docx 解壓後內容超過上限");
            }

            using var stream = entry.Open();
            return new ExtractionResult.Success(ReadParagraphs(stream), "docx");
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or EndOfStreamException)
        {
            return new ExtractionResult.Skipped(SkipReason.Corrupt, $"docx 檔案損毀：{ex.Message}");
        }
    }

    private static string ReadParagraphs(Stream stream)
    {
        var text = new StringBuilder();
        using var reader = XmlReader.Create(stream, Settings);
        var inText = false;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.NamespaceURI == WordNamespace:
                    switch (reader.LocalName)
                    {
                        case "t":
                            inText = !reader.IsEmptyElement;
                            break;
                        case "tab":
                            text.Append('\t');
                            break;
                        case "br":
                        case "cr":
                            text.Append('\n');
                            break;
                    }

                    break;

                case XmlNodeType.EndElement when reader.NamespaceURI == WordNamespace:
                    if (reader.LocalName == "t")
                    {
                        inText = false;
                    }
                    else if (reader.LocalName == "p")
                    {
                        text.Append('\n');
                    }

                    break;

                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace when inText:
                    text.Append(reader.Value);
                    break;
            }
        }

        return text.ToString();
    }
}
