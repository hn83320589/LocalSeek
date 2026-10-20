using LocalSeek.Core.Index;
using LocalSeek.Core.Query;
using LocalSeek.Core.Search;
using LocalSeek.Tests.Reference;
using static LocalSeek.Tests.Properties.Oracle;

namespace LocalSeek.Tests.Properties;

/// <summary>Acceptance #5, #6, #7, #9: the engine agrees with the brute-force reference engine.</summary>
public class SearchProperties
{
    private static readonly TimeZoneInfo Utc8 = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    private sealed record Fixture(List<ReferenceDocument> Docs, MemoryIndex Index, ReferenceEngine Reference)
    {
        public static Fixture Create(int seed, int count)
        {
            var docs = CorpusGen.Documents(new Random(seed), count);
            return new Fixture(docs, MemoryIndex.Build(CorpusGen.ToInputs(docs)), new ReferenceEngine(docs, Utc8));
        }

        public Dictionary<string, int> EngineFrequencies(BoundNode leaf)
        {
            var tf = QueryExecutor.TermFrequencies(Index, leaf);
            return Enumerable.Range(0, Index.DocCount).ToDictionary(d => Index.GetMetadata(d).Path, d => tf[d]);
        }
    }

    private static readonly Lazy<Fixture> Corpus = new(() => Fixture.Create(seed: 5000, count: 320));

    [Fact]
    public void Boolean_hit_sets_match_reference()
    {
        var f = Corpus.Value;
        var checkedQueries = 0;
        var nonEmpty = 0;
        Property.ForAll(seed: 5001, iterations: 2600, random =>
        {
            var query = CorpusGen.Query(random);
            if (!TryBindBoth(query, out var engine, out var reference))
            {
                return;
            }

            checkedQueries++;
            Assert.Equal(reference.ToString(), engine.ToString());
            var expected = f.Reference.Search(reference, int.MaxValue).Hits.Select(h => h.Path).ToHashSet();
            var actual = QueryExecutor.Execute(f.Index, engine, new SearchOptions(int.MaxValue, Utc8));

            Assert.Equal(expected.Count, actual.TotalHits);
            Assert.True(expected.SetEquals(actual.Hits.Select(h => h.Path)), $"Hit sets differ for '{query}'.");
            nonEmpty += expected.Count > 0 ? 1 : 0;
        });

        Assert.True(checkedQueries >= 2000, $"Only {checkedQueries} valid queries were checked.");
        Assert.True(nonEmpty >= checkedQueries / 2, $"Only {nonEmpty}/{checkedQueries} queries had hits; generator too sparse.");
        Assert.True(f.Docs.Count >= 300);
    }

    [Fact]
    public void Scores_and_ranking_match_reference()
    {
        var f = Corpus.Value;
        Property.ForAll(seed: 5002, iterations: 2000, random =>
        {
            var query = CorpusGen.Query(random);
            if (!TryBindBoth(query, out var engine, out var reference))
            {
                return;
            }

            var topK = random.Next(2) == 0 ? 200 : int.MaxValue;
            var expected = f.Reference.Search(reference, topK);
            var actual = QueryExecutor.Execute(f.Index, engine, new SearchOptions(topK, Utc8));

            Assert.Equal(expected.Total, actual.TotalHits);
            Assert.Equal(expected.Hits.Select(h => h.Path), actual.Hits.Select(h => h.Path));
            for (var i = 0; i < expected.Hits.Count; i++)
            {
                AssertClose(expected.Hits[i].Score, actual.Hits[i].Score, query);
            }
        });
    }

    [Fact]
    public void Phrase_occurrence_counts_match_reference()
    {
        var f = Corpus.Value;
        var withHits = 0;
        var crossScript = 0;
        Property.ForAll(seed: 5003, iterations: 2500, random =>
        {
            // Half of the phrases are cut from real documents so most of them have hits.
            string text;
            if (random.Next(2) == 0)
            {
                var source = f.Docs[random.Next(f.Docs.Count)].Body;
                var start = random.Next(Math.Max(1, source.Length));
                text = source.Substring(start, Math.Min(source.Length - start, random.Next(2, 12)));
            }
            else
            {
                text = CorpusGen.Fragment(random) + (random.Next(2) == 0 ? " " : "") + CorpusGen.Fragment(random);
            }

            if (!TryBindBoth($"\"{text.Replace("\"", "")}\"", out var engine, out var reference) || engine is not PhraseQuery)
            {
                return;
            }

            var actual = f.EngineFrequencies(engine);
            Assert.Equal(f.Reference.TermFrequencies(reference), actual);
            withHits += actual.Values.Any(v => v > 0) ? 1 : 0;
            var terms = ((PhraseQuery)engine).Terms;
            crossScript += terms.Any(t => t[0] < 0x80) && terms.Any(t => t[0] >= 0x80) ? 1 : 0;
        });

        Assert.True(withHits >= 300, $"Only {withHits} phrases had hits.");
        Assert.True(crossScript >= 100, $"Only {crossScript} cross-script phrases.");
    }

    [Fact]
    public void Single_char_and_prefix_frequencies_match_reference()
    {
        var f = Corpus.Value;
        Property.ForAll(seed: 5004, iterations: 600, random =>
        {
            var query = random.Next(3) switch
            {
                0 => CorpusGen.CjkChars[random.Next(CorpusGen.CjkChars.Length)],
                1 => PrefixOf(CorpusGen.LatinWords[random.Next(CorpusGen.LatinWords.Length)], random.Next(2, 5)) + "*",
                _ => CorpusGen.LatinWord(random) + CorpusGen.CjkChars[random.Next(CorpusGen.CjkChars.Length)] + "x",
            };
            if (!TryBindBoth(query, out var engine, out var reference))
            {
                return;
            }

            var leaves = QueryExecutor.ScoringLeaves(engine);
            Assert.All(leaves, leaf =>
            {
                var actual = f.EngineFrequencies(leaf);
                Assert.Equal(f.Reference.TermFrequencies(leaf), actual);
                Assert.Equal(f.Reference.DocumentFrequency(leaf), actual.Values.Count(v => v > 0));
            });
        });
    }

    private static string PrefixOf(string word, int length) => word[..Math.Min(word.Length, length)];

    private static void AssertClose(double expected, double actual, string query)
    {
        var error = expected == 0 ? Math.Abs(actual) : Math.Abs(actual - expected) / Math.Abs(expected);
        Assert.True(error <= 1e-9, $"Score {actual} vs reference {expected} (relative error {error}) for '{query}'.");
    }
}
