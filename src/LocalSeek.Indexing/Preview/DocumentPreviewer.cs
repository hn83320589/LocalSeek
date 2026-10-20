using LocalSeek.Core.Query;
using LocalSeek.Core.Search;
using LocalSeek.Indexing.Extraction;
using LocalSeek.Indexing.FileSystem;

namespace LocalSeek.Indexing.Preview;

/// <summary>Snippets of the body and highlights in the file name; Message explains when the body is unavailable.</summary>
public sealed record DocumentPreview(string Path, IReadOnlyList<Snippet> Snippets, IReadOnlyList<Highlight> NameHighlights, string? Message);

/// <summary>
/// Builds the preview of a search hit by extracting the file again: the index stores positions, and
/// re-tokenizing the current text yields exact original-text coordinates for highlighting.
/// </summary>
public sealed class DocumentPreviewer(ISourceFileSystem fileSystem, ContentExtractor extractor)
{
    public DocumentPreview Create(string path, BoundNode query)
    {
        var nameHighlights = SnippetGenerator.Merge(SnippetGenerator.FindHits(System.IO.Path.GetFileName(path), query));
        var entry = fileSystem.GetEntry(path);
        if (entry is not null && extractor.IsTooLarge(entry.Size))
        {
            return new DocumentPreview(path, [], nameHighlights, "檔案超過大小上限，無法預覽");
        }

        return SourceFileReader.Read(fileSystem, path) switch
        {
            ReadOutcome.Success read => extractor.Extract(path, read.Bytes) switch
            {
                ExtractionResult.Success text => new DocumentPreview(path, SnippetGenerator.Create(text.Text, query).Snippets, nameHighlights, null),
                ExtractionResult.Skipped skipped => new DocumentPreview(path, [], nameHighlights, skipped.Message),
                _ => throw new InvalidOperationException("Unknown extraction result."),
            },
            ReadOutcome.Missing => new DocumentPreview(path, [], nameHighlights, "檔案已不存在"),
            ReadOutcome.Locked => new DocumentPreview(path, [], nameHighlights, "檔案正被其他程式鎖定，無法預覽"),
            ReadOutcome.Changed => new DocumentPreview(path, [], nameHighlights, "檔案正在變更，請稍後再試"),
            ReadOutcome.AccessDenied => new DocumentPreview(path, [], nameHighlights, "權限不足，無法讀取檔案"),
            ReadOutcome.Failed failed => new DocumentPreview(path, [], nameHighlights, $"讀取失敗：{failed.Message}"),
            _ => throw new InvalidOperationException("Unknown read outcome."),
        };
    }
}
