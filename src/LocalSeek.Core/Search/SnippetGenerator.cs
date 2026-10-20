using LocalSeek.Core.Query;
using LocalSeek.Core.Text;

namespace LocalSeek.Core.Search;

/// <summary>One occurrence of a scoring term in the original text: [Start, End) and the term's canonical key.</summary>
public sealed record HitRange(int Start, int End, string Key);

/// <summary>A highlighted range: relative to the source text, or to a snippet's text inside a snippet.</summary>
public sealed record Highlight(int Start, int Length);

/// <summary>A window of the source text starting at Start; control characters are replaced by spaces.</summary>
public sealed record Snippet(int Start, string Text, IReadOnlyList<Highlight> Highlights);

public sealed record SnippetResult(IReadOnlyList<Snippet> Snippets, IReadOnlyList<Highlight> Highlights, IReadOnlyList<HitRange> Hits);

/// <summary>
/// Finds the positive query terms in a document's text using the tokenizer's source coordinates,
/// merges overlapping or touching hits into highlights, and picks up to three non-overlapping windows
/// of at most 120 chars (greedy: most not-yet-shown distinct terms first), returned in document order.
/// Window edges never split a surrogate pair.
/// </summary>
public static class SnippetGenerator
{
    public const int MaxSnippets = 3;
    public const int MaxLength = 120;
    private const int LeadingContext = 20;

    public static SnippetResult Create(string text, BoundNode query, int maxSnippets = MaxSnippets, int maxLength = MaxLength)
    {
        var hits = FindHits(text, query);
        var highlights = Merge(hits);
        if (text.Length == 0)
        {
            return new SnippetResult([], highlights, hits);
        }

        var windows = new List<(int Start, int End)>();
        if (highlights.Count == 0)
        {
            windows.Add(Window(text, 0, maxLength));
        }
        else
        {
            var shown = new HashSet<HitRange>();
            while (windows.Count < maxSnippets)
            {
                var best = highlights
                    .Select(h => Window(text, h.Start - LeadingContext, maxLength))
                    .Where(w => !windows.Any(c => w.Start < c.End && c.Start < w.End))
                    .Select(w => (Window: w, Score: Score(hits, shown, w)))
                    .Where(x => x.Score.DistinctTerms > 0)
                    .OrderByDescending(x => x.Score.DistinctTerms)
                    .ThenByDescending(x => x.Score.Hits)
                    .ThenBy(x => x.Window.Start)
                    .FirstOrDefault();
                if (best.Score.DistinctTerms == 0)
                {
                    break;
                }

                windows.Add(best.Window);
                shown.UnionWith(hits.Where(h => Inside(h, best.Window)));
            }
        }

        var snippets = windows
            .OrderBy(w => w.Start)
            .Select(w => new Snippet(
                w.Start,
                Blank(text.AsSpan(w.Start, w.End - w.Start)),
                highlights
                    .Where(h => h.Start < w.End && w.Start < h.Start + h.Length)
                    .Select(h =>
                    {
                        var start = Math.Max(h.Start, w.Start);
                        var end = Math.Min(h.Start + h.Length, w.End);
                        return new Highlight(start - w.Start, end - start);
                    })
                    .ToList()))
            .ToList();
        return new SnippetResult(snippets, highlights, hits);
    }

    /// <summary>Every occurrence of every positive scoring term (term, single char, prefix, phrase).</summary>
    public static IReadOnlyList<HitRange> FindHits(string text, BoundNode query)
    {
        var tokenized = Tokenizer.TokenizeToList(text);
        var byPosition = tokenized.Tokens.ToDictionary(t => t.Position);
        var hits = new List<HitRange>();
        foreach (var leaf in QueryExecutor.ScoringLeaves(query))
        {
            var key = leaf.ToString();
            switch (leaf)
            {
                case TermQuery term:
                    hits.AddRange(tokenized.Tokens
                        .Where(t => t.Kind != TokenKind.CjkSingle && t.Text == term.Term)
                        .Select(t => new HitRange(t.SourceStart, t.SourceEnd, key)));
                    break;
                case CharQuery ideograph:
                    hits.AddRange(tokenized.CjkChars
                        .Where(c => c.Char == ideograph.Char)
                        .Select(c => new HitRange(c.SourceStart, c.SourceEnd, key)));
                    break;
                case PrefixQuery prefix:
                    hits.AddRange(tokenized.Tokens
                        .Where(t => t.Kind == TokenKind.Latin && t.Text.StartsWith(prefix.Prefix, StringComparison.Ordinal))
                        .Select(t => new HitRange(t.SourceStart, t.SourceEnd, key)));
                    break;
                case PhraseQuery phrase:
                    foreach (var first in tokenized.Tokens.Where(t => t.Text == phrase.Terms[0]))
                    {
                        var last = first;
                        var matched = true;
                        for (var i = 1; i < phrase.Terms.Count && matched; i++)
                        {
                            matched = byPosition.TryGetValue(first.Position + i, out last) && last.Text == phrase.Terms[i];
                        }

                        if (matched)
                        {
                            hits.Add(new HitRange(first.SourceStart, last.SourceEnd, key));
                        }
                    }

                    break;
            }
        }

        hits.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        return hits;
    }

    /// <summary>Union of the hit ranges as sorted, disjoint, non-touching highlights.</summary>
    public static IReadOnlyList<Highlight> Merge(IReadOnlyList<HitRange> hits)
    {
        var merged = new List<Highlight>();
        int start = -1, end = -1;
        foreach (var hit in hits.OrderBy(h => h.Start))
        {
            if (start >= 0 && hit.Start <= end)
            {
                end = Math.Max(end, hit.End);
                continue;
            }

            if (start >= 0)
            {
                merged.Add(new Highlight(start, end - start));
            }

            (start, end) = (hit.Start, hit.End);
        }

        if (start >= 0)
        {
            merged.Add(new Highlight(start, end - start));
        }

        return merged;
    }

    private static (int DistinctTerms, int Hits) Score(IReadOnlyList<HitRange> hits, HashSet<HitRange> shown, (int Start, int End) window)
    {
        var inside = hits.Where(h => !shown.Contains(h) && Inside(h, window)).ToList();
        return (inside.Select(h => h.Key).Distinct().Count(), inside.Count);
    }

    private static bool Inside(HitRange hit, (int Start, int End) window) =>
        hit.Start >= window.Start && hit.End <= window.End
        || (hit.Start >= window.Start && hit.Start < window.End && hit.End - hit.Start > window.End - window.Start);

    /// <summary>A window of at most maxLength chars starting near <paramref name="start"/>, kept inside the text and off surrogate pairs.</summary>
    private static (int Start, int End) Window(string text, int start, int maxLength)
    {
        start = Math.Clamp(start, 0, Math.Max(0, text.Length - maxLength));
        if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
        {
            start++;
        }

        var end = Math.Min(text.Length, start + maxLength);
        if (end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return (start, end);
    }

    private static string Blank(ReadOnlySpan<char> text)
    {
        var chars = text.ToArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }
}
