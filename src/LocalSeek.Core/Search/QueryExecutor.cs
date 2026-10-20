using System.Numerics;
using LocalSeek.Core.Index;
using LocalSeek.Core.Query;

namespace LocalSeek.Core.Search;

/// <summary>
/// Term-at-a-time evaluation over dense per-document arrays: every leaf becomes an int[] of weighted
/// term frequencies (tf = body + 2 × name), boolean structure becomes bitsets, and matched documents
/// are scored by the positive leaves. Simple and fast enough for ~10^5 documents.
/// </summary>
public sealed class QueryExecutor
{
    private const int CancellationCheckInterval = 4096;

    private readonly IIndexReader _index;
    private readonly TimeZoneInfo _timeZone;
    private readonly CancellationToken _cancellation;
    private readonly Dictionary<string, int[]> _frequencies = new(StringComparer.Ordinal);
    private readonly int _words;

    private QueryExecutor(IIndexReader index, TimeZoneInfo timeZone, CancellationToken cancellation)
    {
        _index = index;
        _timeZone = timeZone;
        _cancellation = cancellation;
        _words = (index.DocCount + 63) / 64;
    }

    public static SearchResult Execute(IIndexReader index, BoundNode query, SearchOptions options, CancellationToken cancellation = default)
    {
        var executor = new QueryExecutor(index, options.TimeZone ?? TimeZoneInfo.Local, cancellation);
        return executor.Run(query, options.TopK);
    }

    /// <summary>Weighted tf per document for a leaf (term, char, phrase or prefix query).</summary>
    public static int[] TermFrequencies(IIndexReader index, BoundNode leaf, CancellationToken cancellation = default) =>
        new QueryExecutor(index, TimeZoneInfo.Utc, cancellation).Frequencies(leaf);

    /// <summary>Distinct positive scoring leaves (not under an odd number of NOTs), ordered by canonical key.</summary>
    public static IReadOnlyList<BoundNode> ScoringLeaves(BoundNode query)
    {
        var leaves = new SortedDictionary<string, BoundNode>(StringComparer.Ordinal);
        Collect(query, negated: false);
        return leaves.Values.ToList();

        void Collect(BoundNode node, bool negated)
        {
            switch (node)
            {
                case NotQuery n:
                    Collect(n.Operand, !negated);
                    break;
                case AndQuery a:
                    a.Children.ToList().ForEach(c => Collect(c, negated));
                    break;
                case OrQuery o:
                    o.Children.ToList().ForEach(c => Collect(c, negated));
                    break;
                case FilterQuery:
                    break;
                default:
                    if (!negated)
                    {
                        leaves.TryAdd(node.ToString(), node);
                    }

                    break;
            }
        }
    }

    private SearchResult Run(BoundNode query, int topK)
    {
        var n = _index.DocCount;
        var matched = Match(query);
        var averageLength = n == 0 ? 0 : (double)_index.TotalLength / n;

        var leaves = ScoringLeaves(query);
        var tfs = new int[leaves.Count][];
        var idfs = new double[leaves.Count];
        var stats = new List<TermStatistics>(leaves.Count);
        for (var i = 0; i < leaves.Count; i++)
        {
            tfs[i] = Frequencies(leaves[i]);
            var df = CountPositive(tfs[i]);
            idfs[i] = Bm25.Idf(n, df);
            stats.Add(new TermStatistics(leaves[i].ToString(), df, idfs[i]));
        }

        var collector = new TopKCollector(topK);
        var total = 0;
        for (var w = 0; w < matched.Length; w++)
        {
            var bits = matched[w];
            while (bits != 0)
            {
                var doc = (w * 64) + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if ((++total % CancellationCheckInterval) == 0)
                {
                    _cancellation.ThrowIfCancellationRequested();
                }

                var length = _index.GetLength(doc);
                var score = 0.0;
                for (var i = 0; i < tfs.Length; i++)
                {
                    var tf = tfs[i][doc];
                    if (tf > 0)
                    {
                        score += Bm25.TermScore(idfs[i], tf, length, averageLength);
                    }
                }

                collector.Offer(doc, score, _index);
            }
        }

        return new SearchResult(total, collector.ToSortedList(), stats, n, averageLength);
    }

    private ulong[] Match(BoundNode node)
    {
        _cancellation.ThrowIfCancellationRequested();
        switch (node)
        {
            case AndQuery a:
            {
                var result = Match(a.Children[0]);
                foreach (var child in a.Children.Skip(1))
                {
                    var other = Match(child);
                    for (var w = 0; w < _words; w++)
                    {
                        result[w] &= other[w];
                    }
                }

                return result;
            }

            case OrQuery o:
            {
                var result = new ulong[_words];
                foreach (var child in o.Children)
                {
                    var other = Match(child);
                    for (var w = 0; w < _words; w++)
                    {
                        result[w] |= other[w];
                    }
                }

                return result;
            }

            case NotQuery not:
            {
                var result = Match(not.Operand);
                for (var w = 0; w < _words; w++)
                {
                    result[w] = ~result[w];
                }

                ClearTail(result);
                return result;
            }

            case FilterQuery f:
            {
                var result = new ulong[_words];
                for (var doc = 0; doc < _index.DocCount; doc++)
                {
                    if (doc % CancellationCheckInterval == 0)
                    {
                        _cancellation.ThrowIfCancellationRequested();
                    }

                    if (f.Filter.Matches(_index.GetMetadata(doc), _timeZone))
                    {
                        result[doc >> 6] |= 1UL << doc;
                    }
                }

                return result;
            }

            default:
            {
                var tf = Frequencies(node);
                var result = new ulong[_words];
                for (var doc = 0; doc < tf.Length; doc++)
                {
                    if (tf[doc] > 0)
                    {
                        result[doc >> 6] |= 1UL << doc;
                    }
                }

                return result;
            }
        }
    }

    private void ClearTail(ulong[] bits)
    {
        var remainder = _index.DocCount % 64;
        if (remainder != 0)
        {
            bits[^1] &= (1UL << remainder) - 1;
        }
    }

    private int[] Frequencies(BoundNode leaf)
    {
        var key = leaf.ToString();
        if (_frequencies.TryGetValue(key, out var cached))
        {
            return cached;
        }

        _cancellation.ThrowIfCancellationRequested();
        var tf = new int[_index.DocCount];
        switch (leaf)
        {
            case TermQuery t:
                Accumulate(tf, TermSpace.BodyTerm, t.Term, 1);
                Accumulate(tf, TermSpace.NameTerm, t.Term, 2);
                break;
            case CharQuery c:
                Accumulate(tf, TermSpace.BodyChar, c.Char, 1);
                Accumulate(tf, TermSpace.NameChar, c.Char, 2);
                break;
            case PrefixQuery p:
                foreach (var term in _index.GetTermsWithPrefix(TermSpace.BodyTerm, p.Prefix))
                {
                    Accumulate(tf, TermSpace.BodyTerm, term, 1);
                }

                foreach (var term in _index.GetTermsWithPrefix(TermSpace.NameTerm, p.Prefix))
                {
                    Accumulate(tf, TermSpace.NameTerm, term, 2);
                }

                break;
            case PhraseQuery phrase:
                AccumulatePhrase(tf, TermSpace.BodyTerm, phrase.Terms, 1);
                AccumulatePhrase(tf, TermSpace.NameTerm, phrase.Terms, 2);
                break;
            default:
                throw new InvalidOperationException($"{leaf.GetType().Name} is not a term leaf.");
        }

        _frequencies[key] = tf;
        return tf;
    }

    private void Accumulate(int[] tf, TermSpace space, string term, int weight)
    {
        var seen = 0;
        _index.ForEachPosting(space, term, (doc, frequency) =>
        {
            if ((++seen % CancellationCheckInterval) == 0)
            {
                _cancellation.ThrowIfCancellationRequested();
            }

            tf[doc] += weight * frequency;
        });
    }

    /// <summary>
    /// Counts start positions s where Terms[i] occurs at s + i for every i. Documents containing every
    /// term are found from postings alone; positions are then decoded only for those documents.
    /// </summary>
    private void AccumulatePhrase(int[] tf, TermSpace space, IReadOnlyList<string> terms, int weight)
    {
        var distinct = terms.Distinct(StringComparer.Ordinal).ToList();
        ulong[]? candidates = null;
        foreach (var term in distinct)
        {
            _cancellation.ThrowIfCancellationRequested();
            var docs = new ulong[_words];
            var seen = 0;
            _index.ForEachPosting(space, term, (doc, _) =>
            {
                if ((++seen % CancellationCheckInterval) == 0)
                {
                    _cancellation.ThrowIfCancellationRequested();
                }

                docs[doc >> 6] |= 1UL << doc;
            });

            if (candidates is null)
            {
                candidates = docs;
            }
            else
            {
                for (var w = 0; w < _words; w++)
                {
                    candidates[w] &= docs[w];
                }
            }

            if (Array.TrueForAll(candidates, w => w == 0))
            {
                return;
            }
        }

        var lists = new Dictionary<string, PositionLists>(StringComparer.Ordinal);
        foreach (var term in distinct)
        {
            _cancellation.ThrowIfCancellationRequested();
            var list = new PositionLists(_index.DocCount);
            _index.ForEachPositions(space, term, list.Add, doc => (candidates![doc >> 6] & (1UL << doc)) != 0);
            lists[term] = list;
        }

        var perTerm = terms.Select(t => lists[t]).ToArray();
        var cursors = new int[perTerm.Length];
        var checkedDocs = 0;
        for (var w = 0; w < candidates!.Length; w++)
        {
            var bits = candidates[w];
            while (bits != 0)
            {
                var doc = (w * 64) + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if ((++checkedDocs % 1024) == 0)
                {
                    _cancellation.ThrowIfCancellationRequested();
                }

                tf[doc] += weight * CountPhraseStarts(perTerm, doc, cursors);
            }
        }
    }

    /// <summary>Start positions s with perTerm[i] at s + i for all i; one forward pass over each sorted list.</summary>
    private static int CountPhraseStarts(PositionLists[] perTerm, int doc, int[] cursors)
    {
        Array.Clear(cursors);
        var first = perTerm[0].Of(doc);
        var count = 0;
        foreach (var start in first)
        {
            var all = true;
            for (var i = 1; i < perTerm.Length && all; i++)
            {
                var positions = perTerm[i].Of(doc);
                var target = start + i;
                ref var cursor = ref cursors[i];
                while (cursor < positions.Length && positions[cursor] < target)
                {
                    cursor++;
                }

                all = cursor < positions.Length && positions[cursor] == target;
            }

            if (all)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Positions of one term for many documents, packed into one array.</summary>
    private sealed class PositionLists(int documentCount)
    {
        private readonly int[] _start = new int[documentCount];
        private readonly int[] _length = new int[documentCount];
        private int[] _positions = new int[1024];
        private int _count;

        public void Add(int doc, ReadOnlySpan<int> positions)
        {
            if (_count + positions.Length > _positions.Length)
            {
                Array.Resize(ref _positions, Math.Max(_count + positions.Length, _positions.Length * 2));
            }

            positions.CopyTo(_positions.AsSpan(_count));
            _start[doc] = _count;
            _length[doc] = positions.Length;
            _count += positions.Length;
        }

        public ReadOnlySpan<int> Of(int doc) => _positions.AsSpan(_start[doc], _length[doc]);
    }

    private static int CountPositive(int[] values)
    {
        var count = 0;
        foreach (var v in values)
        {
            if (v > 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Keeps the best K hits: score descending, then full path ascending (OrdinalIgnoreCase).</summary>
    private sealed class TopKCollector(int k)
    {
        private readonly PriorityQueue<SearchHit, SearchHit> _worstFirst = new(Comparer<SearchHit>.Create((a, b) => Compare(b, a)));

        public static int Compare(SearchHit a, SearchHit b)
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        }

        public void Offer(int doc, double score, IIndexReader index)
        {
            if (k <= 0)
            {
                return;
            }

            if (_worstFirst.Count == k)
            {
                var worst = _worstFirst.Peek();
                if (score < worst.Score)
                {
                    return;
                }
            }

            var hit = new SearchHit(doc, index.GetMetadata(doc).Path, score);
            if (_worstFirst.Count < k)
            {
                _worstFirst.Enqueue(hit, hit);
            }
            else if (Compare(hit, _worstFirst.Peek()) < 0)
            {
                _worstFirst.DequeueEnqueue(hit, hit);
            }
        }

        public List<SearchHit> ToSortedList()
        {
            var list = _worstFirst.UnorderedItems.Select(x => x.Element).ToList();
            list.Sort(Compare);
            return list;
        }
    }
}
