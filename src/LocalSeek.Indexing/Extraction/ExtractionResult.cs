namespace LocalSeek.Indexing.Extraction;

public enum SkipReason
{
    TooLarge,
    Binary,
    UnknownEncoding,
    Unsupported,
    Corrupt,
}

/// <summary>Outcome of extracting text from a file's bytes.</summary>
public abstract record ExtractionResult
{
    private ExtractionResult()
    {
    }

    public sealed record Success(string Text, string Encoding) : ExtractionResult;

    public sealed record Skipped(SkipReason Reason, string Message) : ExtractionResult;
}

public sealed record ExtractionOptions
{
    public long MaxFileBytes { get; init; } = 20L * 1024 * 1024;

    /// <summary>A sample with more NUL bytes than this fraction is binary.</summary>
    public double BinaryNulRatio { get; init; } = 0.01;

    public int BinarySampleBytes { get; init; } = 64 * 1024;

    /// <summary>Upper bound for the uncompressed main part of a .docx (zip-bomb guard).</summary>
    public long MaxDocxPartBytes { get; init; } = 200L * 1024 * 1024;
}
