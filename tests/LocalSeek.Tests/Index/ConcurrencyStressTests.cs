using System.Collections.Concurrent;
using LocalSeek.Core.Index;
using LocalSeek.Core.Query;
using LocalSeek.Core.Search;
using LocalSeek.Tests.Fakes;

namespace LocalSeek.Tests.Index;

/// <summary>
/// Acceptance #17: indexing, merging and searching at the same time. The writer's operation sequence is
/// generated from a fixed seed; searchers check invariants that every consistent snapshot satisfies.
/// </summary>
public class ConcurrencyStressTests
{
    private const int StableCount = 50;
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Search_during_indexing_and_merging_sees_consistent_snapshots()
    {
        using var writer = IndexWriter.Open(
            new FakeIndexStorage(),
            new IndexWriterOptions { RamBufferBytes = 3000, MergePolicy = new MergePolicy { MergeFactor = 3, MinSegmentDocs = 5 } });
        for (var i = 0; i < StableCount; i++)
        {
            writer.AddOrUpdate(new DocumentInput($@"C:\stable\{i}.txt", 1, T0, $"stable 台灣 s{i}"));
        }

        writer.Commit();

        var errors = new ConcurrentQueue<Exception>();
        using var done = new CancellationTokenSource();
        var searches = 0;

        var indexer = Task.Run(() =>
        {
            var random = new Random(7001);
            var churn = new Dictionary<int, string>();
            var generation = 0;
            for (var step = 0; step < 400; step++)
            {
                var id = random.Next(40);
                if (!churn.TryGetValue(id, out var path))
                {
                    path = $@"C:\churn\{id}_{generation++}.txt";
                    churn[id] = path;
                    writer.AddOrUpdate(new DocumentInput(path, 1, T0, $"churn 資料 cid{id} v{step}"));
                }
                else
                {
                    switch (random.Next(3))
                    {
                        case 0:
                            writer.Delete(path);
                            churn.Remove(id);
                            break;
                        case 1:
                            var renamed = $@"C:\churn\{id}_{generation++}.txt";
                            writer.Rename(path, renamed, 1, T0);
                            churn[id] = renamed;
                            break;
                        default:
                            writer.AddOrUpdate(new DocumentInput(path, 1, T0, $"churn 資料 cid{id} v{step}"));
                            break;
                    }
                }

                if (random.Next(3) == 0)
                {
                    writer.Commit();
                }
            }

            writer.Commit();
        });

        var merger = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                try
                {
                    if (!writer.MaybeMerge())
                    {
                        Thread.Yield();
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                    return;
                }
            }
        });

        var searchers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            var stable = QueryBinder.Bind(QueryParser.Parse("stable"));
            var churn = QueryBinder.Bind(QueryParser.Parse("churn"));
            while (!done.IsCancellationRequested)
            {
                try
                {
                    using var snapshot = writer.AcquireSnapshot();
                    var stableHits = QueryExecutor.Execute(snapshot, stable, SearchOptions.Default);
                    var churnHits = QueryExecutor.Execute(snapshot, churn, new SearchOptions(int.MaxValue));

                    Assert.Equal(StableCount, stableHits.TotalHits);
                    Assert.Equal(StableCount, stableHits.Hits.Select(h => h.Path).Distinct().Count());
                    var ids = churnHits.Hits.Select(h => Path.GetFileName(h.Path).Split('_')[0]).ToList();
                    Assert.Equal(ids.Count, ids.Distinct().Count());
                    Assert.Equal(snapshot.DocCount, StableCount + churnHits.TotalHits);
                    var paths = Enumerable.Range(0, snapshot.DocCount).Select(d => snapshot.GetMetadata(d).Path).ToList();
                    Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                    Assert.Equal(churnHits.Hits, QueryExecutor.Execute(snapshot, churn, new SearchOptions(int.MaxValue)).Hits);
                    Interlocked.Increment(ref searches);
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                    return;
                }
            }
        })).ToList();

        await indexer;
        await done.CancelAsync();
        await merger;
        await Task.WhenAll(searchers);

        Assert.Empty(errors);
        Assert.True(searches > 20, $"Only {searches} searches ran.");
        Assert.True(writer.GetStatistics().MergesCompleted > 0, "No merge ran during the stress test.");
    }
}
