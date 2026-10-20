using System.Diagnostics;
using LocalSeek.Core.Index;
using LocalSeek.Core.Query;
using LocalSeek.Core.Search;
using LocalSeek.Indexing.Preview;

namespace LocalSeek.App.ViewModels;

/// <summary>Runs queries against the latest committed snapshot on a thread-pool thread.</summary>
public sealed class IndexSearchService(Func<IIndexReader> acquireReader, TimeZoneInfo timeZone, int topK = 200) : ISearchService
{
    public Task<SearchOutcome> SearchAsync(string query, CancellationToken cancellation) =>
        Task.Run<SearchOutcome>(
            () =>
            {
                var stopwatch = Stopwatch.StartNew();
                BoundNode bound;
                try
                {
                    bound = QueryBinder.Bind(QueryParser.Parse(query));
                }
                catch (QuerySyntaxException ex)
                {
                    return new SearchOutcome.Invalid(ex.Reason, ex.Position, ex.Length);
                }

                var reader = acquireReader();
                try
                {
                    var result = QueryExecutor.Execute(reader, bound, new SearchOptions(topK, timeZone), cancellation);
                    var items = result.Hits
                        .Select(h =>
                        {
                            var metadata = reader.GetMetadata(h.Doc);
                            return new SearchResultItem(h.Path, h.Score, metadata.Size, metadata.LastWriteUtc);
                        })
                        .ToList();
                    return new SearchOutcome.Success(bound, items, result.TotalHits, stopwatch.Elapsed);
                }
                finally
                {
                    (reader as IDisposable)?.Dispose();
                }
            },
            cancellation);
}

public sealed class PreviewService(DocumentPreviewer previewer) : IPreviewService
{
    public Task<DocumentPreview> GetPreviewAsync(string path, BoundNode query, CancellationToken cancellation) =>
        Task.Run(() => previewer.Create(path, query), cancellation);
}
