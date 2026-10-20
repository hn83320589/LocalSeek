using LocalSeek.Core.Index;
using LocalSeek.Core.Query;
using LocalSeek.Core.Search;
using LocalSeek.Tests.Fakes;
using LocalSeek.Tests.Reference;

namespace LocalSeek.Tests.Properties;

/// <summary>Acceptance #10 and #16: statistics after arbitrary changes, and merging.</summary>
public class IndexProperties
{
    private static readonly MergePolicy SmallPolicy = new() { MergeFactor = 3, MinSegmentDocs = 4 };

    /// <summary>#10: after any sequence of add/modify/delete/rename (+ merges), scores equal a reference built from live documents only.</summary>
    [Fact]
    public void Scores_always_reflect_only_live_documents()
    {
        Property.ForAll(seed: 6001, iterations: 25, random =>
        {
            var pool = CorpusGen.Documents(random, 80);
            var live = new Dictionary<string, ReferenceDocument>(StringComparer.OrdinalIgnoreCase);
            using var writer = IndexWriter.Open(new FakeIndexStorage(), new IndexWriterOptions { RamBufferBytes = 4000, MergePolicy = SmallPolicy });
            var renames = 0;

            for (var step = 0; step < 60; step++)
            {
                var existing = live.Keys.ToList();
                var op = existing.Count == 0 ? 0 : random.Next(4);
                switch (op)
                {
                    case 0:
                    {
                        var doc = pool[random.Next(pool.Count)];
                        live[doc.Path] = doc;
                        writer.AddOrUpdate(new DocumentInput(doc.Path, doc.Size, doc.LastWriteUtc, doc.Body));
                        break;
                    }

                    case 1:
                    {
                        var path = existing[random.Next(existing.Count)];
                        var modified = live[path] with { Body = pool[random.Next(pool.Count)].Body, Size = random.Next(5000) };
                        live[path] = modified;
                        writer.AddOrUpdate(new DocumentInput(modified.Path, modified.Size, modified.LastWriteUtc, modified.Body));
                        break;
                    }

                    case 2:
                    {
                        var path = existing[random.Next(existing.Count)];
                        live.Remove(path);
                        Assert.True(writer.Delete(path));
                        break;
                    }

                    default:
                    {
                        var path = existing[random.Next(existing.Count)];
                        var old = live[path];
                        var renamed = old with { Path = $@"C:\renamed\{renames++}_{CorpusGen.Fragment(random)}.md" };
                        live.Remove(path);
                        live[renamed.Path] = renamed;
                        Assert.True(writer.Rename(old.Path, renamed.Path, renamed.Size, renamed.LastWriteUtc));
                        break;
                    }
                }

                if (random.Next(4) == 0)
                {
                    writer.Commit();
                    while (random.Next(2) == 0 && writer.MaybeMerge())
                    {
                    }

                    Verify(writer, live, random);
                }
            }

            writer.Commit();
            Verify(writer, live, random);
        });
    }

    private static void Verify(IndexWriter writer, Dictionary<string, ReferenceDocument> live, Random random)
    {
        var reference = new ReferenceEngine(live.Values, Oracle.Utc8);
        using var snapshot = writer.AcquireSnapshot();
        Assert.Equal(live.Count, snapshot.DocCount);
        for (var q = 0; q < 8; q++)
        {
            Oracle.AssertSameResults(snapshot, reference, CorpusGen.Query(random, depth: 2));
        }
    }

    /// <summary>#16: segment count is O(log N); merging changes no result or score; tombstones are gone afterwards.</summary>
    [Fact]
    public void Merging_preserves_results_and_keeps_segment_count_logarithmic()
    {
        Property.ForAll(seed: 6002, iterations: 4, random =>
        {
            var docs = CorpusGen.Documents(random, 500);
            using var writer = IndexWriter.Open(new FakeIndexStorage(), new IndexWriterOptions { RamBufferBytes = 3000, MergePolicy = SmallPolicy });
            var counts = new List<(int Docs, int Segments)>();
            for (var i = 0; i < docs.Count; i++)
            {
                writer.AddOrUpdate(new DocumentInput(docs[i].Path, docs[i].Size, docs[i].LastWriteUtc, docs[i].Body));
                if (i % 10 == 9)
                {
                    writer.Commit();
                    while (writer.MaybeMerge())
                    {
                    }

                    var stats = writer.GetStatistics();
                    var bound = (SmallPolicy.MergeFactor - 1) * (SmallPolicy.LevelOf(stats.LiveDocuments) + 1);
                    Assert.True(stats.SegmentCount <= bound, $"{stats.SegmentCount} segments for {stats.LiveDocuments} docs exceeds {bound}.");
                    counts.Add((stats.LiveDocuments, stats.SegmentCount));
                }
            }

            Assert.True(counts[^1].Segments < counts[^1].Docs / 20, "Merging must keep far fewer segments than flushes.");

            foreach (var doc in docs.Where(_ => random.Next(5) == 0))
            {
                writer.Delete(doc.Path);
            }

            writer.Commit();
            var queries = Enumerable.Range(0, 60).Select(_ => CorpusGen.Query(random)).ToList();
            var before = Results(writer, queries);
            var dumpBefore = Index.LogicalDump.Of(writer);

            Assert.True(writer.ForceMerge());

            Assert.Equal(0, writer.GetStatistics().DeletedDocuments);
            Assert.Equal(dumpBefore, Index.LogicalDump.Of(writer));
            Assert.Equal(before, Results(writer, queries));
        });
    }

    private static List<string> Results(IndexWriter writer, List<string> queries)
    {
        using var snapshot = writer.AcquireSnapshot();
        var results = new List<string>();
        foreach (var query in queries)
        {
            BoundNode bound;
            try
            {
                bound = QueryBinder.Bind(QueryParser.Parse(query));
            }
            catch (QuerySyntaxException)
            {
                continue;
            }

            var result = QueryExecutor.Execute(snapshot, bound, new SearchOptions(int.MaxValue, Oracle.Utc8));
            results.Add($"{query} => {result.TotalHits}: " + string.Join(";", result.Hits.Select(h => $"{h.Path}={h.Score:R}")));
        }

        return results;
    }
}
