using System.Text.Json;
using LocalSeek.Core.Index;
using LocalSeek.Core.Query;
using LocalSeek.Core.Search;
using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.Extraction;
using LocalSeek.Indexing.FileSystem;
using LocalSeek.Indexing.Pipeline;
using LocalSeek.Tests.Fakes;

namespace LocalSeek.Tests.Samples;

/// <summary>
/// The sample corpus in samples/corpus, indexed from the real files, must reproduce the hand-calculated
/// tokens, lengths, df, idf, scores and ranking in samples/expected (see samples/expected/README.md).
/// </summary>
public sealed class SampleCorpusTests : IAsyncLifetime
{
    private static readonly string SamplesDirectory = FindSamples();

    private readonly DiagnosticsLog _diagnostics = new(TimeProvider.System);
    private IndexWriter _writer = null!;
    private JsonElement _expected;

    public async Task InitializeAsync()
    {
        _expected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(SamplesDirectory, "expected", "expected.json"))).RootElement;
        _writer = IndexWriter.Open(new FakeIndexStorage());
        var engine = new IndexingEngine(_writer, new PhysicalSourceFileSystem(), new ContentExtractor(new ExtractionOptions()), _diagnostics, TimeProvider.System, new IndexingOptions
        {
            Roots = [Path.Combine(SamplesDirectory, "corpus")],
        });
        engine.RequestFullScan();
        await engine.ProcessAsync(includeNotYetDue: true);
    }

    public Task DisposeAsync()
    {
        _writer.Dispose();
        return Task.CompletedTask;
    }

    public static TheoryData<string> QueryIds()
    {
        var data = new TheoryData<string>();
        var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(SamplesDirectory, "expected", "expected.json"))).RootElement;
        foreach (var query in expected.GetProperty("queries").EnumerateArray())
        {
            data.Add(query.GetProperty("id").GetString()!);
        }

        return data;
    }

    [Fact]
    public void Documents_lengths_and_skipped_files_match_the_hand_calculation()
    {
        using var snapshot = _writer.AcquireSnapshot();
        var lengths = Enumerable.Range(0, snapshot.DocCount).ToDictionary(d => Path.GetFileName(snapshot.GetMetadata(d).Path), snapshot.GetLength);

        Assert.Equal(_expected.GetProperty("documentCount").GetInt32(), snapshot.DocCount);
        Assert.Equal(_expected.GetProperty("totalLength").GetInt64(), snapshot.TotalLength);
        foreach (var expected in _expected.GetProperty("lengths").EnumerateObject())
        {
            Assert.Equal(expected.Value.GetInt32(), lengths[expected.Name]);
        }

        var skipped = _diagnostics.Entries().Select(e => $"{Path.GetFileName(e.Path)}:{e.Kind}").ToList();
        Assert.Equal(["13-binary.txt:Binary", "14-unknown-encoding.txt:UnknownEncoding"], skipped);
    }

    [Theory]
    [MemberData(nameof(QueryIds))]
    public void Query_results_match_the_hand_calculation(string id)
    {
        var expected = _expected.GetProperty("queries").EnumerateArray().Single(q => q.GetProperty("id").GetString() == id);
        using var snapshot = _writer.AcquireSnapshot();

        var result = QueryExecutor.Execute(snapshot, QueryBinder.Bind(QueryParser.Parse(expected.GetProperty("query").GetString()!)), SearchOptions.Default);

        Assert.Equal(expected.GetProperty("totalHits").GetInt32(), result.TotalHits);
        var hits = expected.GetProperty("hits").EnumerateArray().ToList();
        Assert.Equal(hits.Select(h => h.GetProperty("file").GetString()), result.Hits.Select(h => Path.GetFileName(h.Path)));
        for (var i = 0; i < hits.Count; i++)
        {
            AssertClose(hits[i].GetProperty("score").GetDouble(), result.Hits[i].Score);
        }

        var terms = expected.GetProperty("terms").EnumerateArray().ToList();
        Assert.Equal(terms.Select(t => t.GetProperty("key").GetString()).Order(StringComparer.Ordinal), result.Terms.Select(t => t.Key));
        foreach (var term in terms)
        {
            var actual = result.Terms.Single(t => t.Key == term.GetProperty("key").GetString());
            Assert.Equal(term.GetProperty("df").GetInt32(), actual.DocumentFrequency);
            AssertClose(term.GetProperty("idf").GetDouble(), actual.Idf);
        }
    }

    private static void AssertClose(double expected, double actual)
    {
        var error = expected == 0 ? Math.Abs(actual) : Math.Abs(actual - expected) / Math.Abs(expected);
        Assert.True(error <= 1e-9, $"{actual} vs hand-calculated {expected}");
    }

    private static string FindSamples()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "samples");
            if (Directory.Exists(Path.Combine(candidate, "corpus")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("samples/corpus not found above the test output directory.");
    }
}
