using LocalSeek.Core.Query;
using LocalSeek.Core.Search;
using LocalSeek.Tests.Reference;

namespace LocalSeek.Tests.Properties;

/// <summary>Acceptance #18.</summary>
public class SnippetProperties
{
    [Fact]
    public void Snippets_and_highlights_are_bounded_ordered_exact_and_merged()
    {
        var checkedHits = 0;
        Property.ForAll(seed: 9001, iterations: 1500, random =>
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(0, 80)).Select(_ =>
                (random.Next(6) == 0 ? TextGen.Text(random, TextGen.All, 3) : CorpusGen.Fragment(random)) + (random.Next(3) == 0 ? "\n" : " ")));
            if (!Oracle.TryBindBoth(CorpusGen.Query(random, depth: 2), out var query, out _))
            {
                return;
            }

            var result = SnippetGenerator.Create(text, query);

            // Snippets: count, length, order, no overlap, text taken from the source, no split surrogates.
            Assert.True(result.Snippets.Count <= SnippetGenerator.MaxSnippets);
            for (var i = 0; i < result.Snippets.Count; i++)
            {
                var s = result.Snippets[i];
                Assert.InRange(s.Text.Length, 1, SnippetGenerator.MaxLength);
                Assert.Equal(Blank(text.Substring(s.Start, s.Text.Length)), s.Text);
                AssertNotSplit(text, s.Start);
                AssertNotSplit(text, s.Start + s.Text.Length);
                if (i > 0)
                {
                    Assert.True(result.Snippets[i - 1].Start + result.Snippets[i - 1].Text.Length <= s.Start);
                }

                foreach (var h in s.Highlights)
                {
                    Assert.True(h.Start >= 0 && h.Length > 0 && h.Start + h.Length <= s.Text.Length);
                }
            }

            // Hits: per term, exactly as many as the reference counts in the body; each range contains the term.
            var reference = new ReferenceEngine([new ReferenceDocument(@"C:\x\-", 0, DateTime.UnixEpoch, text)], Oracle.Utc8);
            foreach (var leaf in QueryExecutor.ScoringLeaves(query))
            {
                var hits = result.Hits.Where(h => h.Key == leaf.ToString()).ToList();
                Assert.Equal(reference.TermFrequency(leaf, @"C:\x\-"), hits.Count);
                foreach (var hit in hits)
                {
                    AssertNotSplit(text, hit.Start);
                    AssertNotSplit(text, hit.End);
                    var normalized = ReferenceTokenizer.Normalize(text[hit.Start..hit.End]);
                    var expected = leaf switch
                    {
                        TermQuery t => t.Term,
                        CharQuery c => c.Char,
                        PrefixQuery p => p.Prefix,
                        PhraseQuery p => p.Terms[0],
                        _ => throw new InvalidOperationException(),
                    };
                    Assert.Contains(expected, normalized, StringComparison.Ordinal);
                    checkedHits++;
                }
            }

            // Highlights: exactly the union of hit ranges, sorted, never overlapping or touching.
            var union = new bool[text.Length];
            foreach (var hit in result.Hits)
            {
                Array.Fill(union, true, hit.Start, hit.End - hit.Start);
            }

            var highlighted = new bool[text.Length];
            for (var i = 0; i < result.Highlights.Count; i++)
            {
                var h = result.Highlights[i];
                Array.Fill(highlighted, true, h.Start, h.Length);
                if (i > 0)
                {
                    Assert.True(result.Highlights[i - 1].Start + result.Highlights[i - 1].Length < h.Start);
                }
            }

            Assert.Equal(union, highlighted);
        });

        Property.AssertCoverage(checkedHits > 1000, $"Only {checkedHits} hits checked.");
    }

    private static string Blank(string s) => new(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static void AssertNotSplit(string text, int boundary) =>
        Assert.False(boundary > 0 && boundary < text.Length && char.IsHighSurrogate(text[boundary - 1]) && char.IsLowSurrogate(text[boundary]), $"Boundary {boundary} splits a surrogate pair.");
}
