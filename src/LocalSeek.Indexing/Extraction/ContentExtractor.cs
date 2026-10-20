namespace LocalSeek.Indexing.Extraction;

/// <summary>Chooses the extractor by file extension. Thread-safe.</summary>
public sealed class ContentExtractor(ExtractionOptions options)
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".csv", ".log", ".json", ".xml" };
    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm" };
    private const string DocxExtension = ".docx";

    private long _extractionCount;

    public ExtractionOptions Options { get; } = options;

    /// <summary>Number of extractions performed; lets tests assert that renames are not re-extracted.</summary>
    public long ExtractionCount => Interlocked.Read(ref _extractionCount);

    public static bool IsSupported(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return TextExtensions.Contains(extension) || HtmlExtensions.Contains(extension) || extension.Equals(DocxExtension, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsTooLarge(long size) => size > Options.MaxFileBytes;

    public ExtractionResult Extract(string fileName, byte[] content)
    {
        Interlocked.Increment(ref _extractionCount);
        if (IsTooLarge(content.LongLength))
        {
            return new ExtractionResult.Skipped(SkipReason.TooLarge, $"檔案超過大小上限 {Options.MaxFileBytes / (1024 * 1024)} MB");
        }

        var extension = Path.GetExtension(fileName);
        if (extension.Equals(DocxExtension, StringComparison.OrdinalIgnoreCase))
        {
            return DocxTextExtractor.Extract(content, Options);
        }

        if (!TextExtensions.Contains(extension) && !HtmlExtensions.Contains(extension))
        {
            return new ExtractionResult.Skipped(SkipReason.Unsupported, $"不支援的副檔名 {extension}");
        }

        var decoded = TextDecoder.Decode(content, Options);
        return decoded is ExtractionResult.Success success && HtmlExtensions.Contains(extension)
            ? success with { Text = HtmlTextExtractor.ToText(success.Text) }
            : decoded;
    }
}
