namespace LocalSeek.Core.Index;

public sealed record IndexWriterOptions
{
    /// <summary>RAM buffer size before the pending segment is flushed to disk.</summary>
    public long RamBufferBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>How many most recent commit points keep their files (fallback if the newest is damaged).</summary>
    public int RetainedCommits { get; init; } = 2;
}

/// <summary>A document whose fields are already tokenized (done in parallel outside the writer lock).</summary>
public sealed record PreparedDocument(string Path, long Size, DateTime LastWriteUtc, ulong ContentHash, AnalyzedField Body, AnalyzedField Name)
{
    public static PreparedDocument Create(DocumentAnalyzer analyzer, DocumentInput input) =>
        new(input.Path, input.Size, input.LastWriteUtc, input.ContentHash, analyzer.Analyze(input.Body), analyzer.Analyze(DocumentAnalyzer.FileNameOf(input.Path)));
}

/// <summary>What the index remembers about a live file, used for change detection.</summary>
public sealed record IndexedEntry(string Path, long Size, DateTime LastWriteUtc, ulong ContentHash);

public enum IndexIssueKind
{
    ManifestUnreadable,
    FormatVersionMismatch,
    SegmentCorrupt,
    SegmentMissing,
    DeletionsCorrupt,
    DanglingDocuments,
    FullRebuildRequired,
}

/// <summary>A problem found while opening or verifying the index, and what was done about it.</summary>
public sealed record IndexIssue(IndexIssueKind Kind, string File, string Message, int AffectedDocuments = 0);

public sealed record IndexOpenReport(IReadOnlyList<IndexIssue> Issues)
{
    public bool HasIssues => Issues.Count > 0;

    public bool FullRebuildRequired => Issues.Any(i => i.Kind == IndexIssueKind.FullRebuildRequired);
}

public sealed record IndexStatistics(long Generation, int LiveDocuments, int SegmentCount, int DeletedDocuments, long IndexBytes, int PendingFileDeletes);
