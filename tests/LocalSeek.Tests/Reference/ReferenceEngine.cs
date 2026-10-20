using LocalSeek.Core.Query;

namespace LocalSeek.Tests.Reference;

public sealed record ReferenceDocument(string Path, long Size, DateTime LastWriteUtc, string Body);

/// <summary>
/// Brute-force search engine used as the oracle in property tests. It scans every document in memory,
/// tokenizes with <see cref="ReferenceTokenizer"/>, and computes scores with its own formula code.
/// Only data types (syntax tree, bound-query records, QuerySyntaxException) are shared with Core.
/// </summary>
public sealed class ReferenceEngine
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly List<Doc> _docs;
    private readonly TimeZoneInfo _timeZone;

    public ReferenceEngine(IEnumerable<ReferenceDocument> documents, TimeZoneInfo timeZone)
    {
        _timeZone = timeZone;
        _docs = documents
            .Select(d => new Doc(d, ReferenceTokenizer.Tokenize(System.IO.Path.GetFileName(d.Path)), ReferenceTokenizer.Tokenize(d.Body)))
            .ToList();
    }

    public int Count => _docs.Count;

    private sealed record Doc(ReferenceDocument Source, ReferenceTokenizer.RefTokenized Name, ReferenceTokenizer.RefTokenized Body)
    {
        public int Length => Body.Length + (2 * Name.Length);
    }

    // ---------------- binding ----------------

    public static BoundNode Bind(QueryNode root)
    {
        var bound = BindNode(root) ?? throw new QuerySyntaxException("查詢沒有可用的詞項或篩選條件", root.Start, root.Length);

        // Bounded iff the query is false when every positive condition is false.
        if (EvaluateWithAllLeavesFalse(bound))
        {
            throw new QuerySyntaxException("查詢不可只有排除條件（NOT）", root.Start, root.Length);
        }

        return bound;
    }

    private static bool EvaluateWithAllLeavesFalse(BoundNode node) => node switch
    {
        NotQuery n => !EvaluateWithAllLeavesFalse(n.Operand),
        AndQuery a => a.Children.All(EvaluateWithAllLeavesFalse),
        OrQuery o => o.Children.Any(EvaluateWithAllLeavesFalse),
        _ => false,
    };

    private static BoundNode? BindNode(QueryNode node)
    {
        switch (node)
        {
            case WordNode w:
            {
                var tokens = ReferenceTokenizer.Tokenize(w.Text).Tokens;
                if (tokens.Count == 0)
                {
                    return null;
                }

                if (tokens.Count == 1)
                {
                    return Leaf(tokens[0]);
                }

                if (tokens.Exists(t => t.Kind == "CjkSingle"))
                {
                    return new AndQuery(tokens.Select(Leaf).ToList());
                }

                return new PhraseQuery(tokens.Select(t => t.Text).ToList());
            }

            case PhraseNode p:
            {
                var tokens = ReferenceTokenizer.Tokenize(p.Text).Tokens;
                if (tokens.Exists(t => t.Kind == "CjkSingle"))
                {
                    throw new QuerySyntaxException("片語不可包含單字詞元", p.Start, p.Length);
                }

                if (tokens.Count == 0)
                {
                    return null;
                }

                return tokens.Count == 1 ? new TermQuery(tokens[0].Text) : new PhraseQuery(tokens.Select(t => t.Text).ToList());
            }

            case PrefixNode p:
            {
                var tokens = ReferenceTokenizer.Tokenize(p.Text).Tokens;
                if (tokens.Count != 1 || tokens[0].Kind != "Latin" || tokens[0].Text.Length < 2)
                {
                    throw new QuerySyntaxException("前綴至少需要 2 個 Latin 字元", p.Start, p.Length);
                }

                return new PrefixQuery(tokens[0].Text);
            }

            case FilterNode f:
                return new FilterQuery(f.Filter);

            case NotNode n:
                return BindNode(n.Operand) is { } operand ? new NotQuery(operand) : null;

            case AndNode a:
            {
                var children = a.Children.Select(BindNode).Where(c => c is not null).Cast<BoundNode>().ToList();
                return children.Count == 0 ? null : children.Count == 1 ? children[0] : new AndQuery(children);
            }

            case OrNode o:
            {
                var children = o.Children.Select(BindNode).Where(c => c is not null).Cast<BoundNode>().ToList();
                return children.Count == 0 ? null : children.Count == 1 ? children[0] : new OrQuery(children);
            }

            default:
                throw new InvalidOperationException(node.GetType().Name);
        }
    }

    private static BoundNode Leaf(ReferenceTokenizer.RefToken token) =>
        token.Kind == "CjkSingle" ? new CharQuery(token.Text) : new TermQuery(token.Text);

    // ---------------- matching and term frequencies ----------------

    public int TermFrequency(BoundNode leaf, string path) =>
        TermFrequency(leaf, _docs.Single(d => d.Source.Path == path));

    public IReadOnlyDictionary<string, int> TermFrequencies(BoundNode leaf) =>
        _docs.ToDictionary(d => d.Source.Path, d => TermFrequency(leaf, d));

    private static int TermFrequency(BoundNode leaf, Doc doc) =>
        FieldFrequency(leaf, doc.Body) + (2 * FieldFrequency(leaf, doc.Name));

    private static int FieldFrequency(BoundNode leaf, ReferenceTokenizer.RefTokenized field)
    {
        switch (leaf)
        {
            case TermQuery t:
                return field.Tokens.Count(x => x.Kind != "CjkSingle" && x.Text == t.Term);
            case CharQuery c:
                return field.CjkCharCounts.GetValueOrDefault(c.Char);
            case PrefixQuery p:
                return field.Tokens.Count(x => x.Kind == "Latin" && x.Text.StartsWith(p.Prefix, StringComparison.Ordinal));
            case PhraseQuery ph:
            {
                var byPosition = field.Tokens.ToDictionary(x => x.Position, x => x.Text);
                var count = 0;
                foreach (var start in byPosition.Keys)
                {
                    var ok = true;
                    for (var i = 0; i < ph.Terms.Count && ok; i++)
                    {
                        ok = byPosition.TryGetValue(start + i, out var text) && text == ph.Terms[i];
                    }

                    if (ok)
                    {
                        count++;
                    }
                }

                return count;
            }

            default:
                throw new InvalidOperationException(leaf.GetType().Name);
        }
    }

    private bool Matches(BoundNode node, Doc doc) => node switch
    {
        AndQuery a => a.Children.All(c => Matches(c, doc)),
        OrQuery o => o.Children.Any(c => Matches(c, doc)),
        NotQuery n => !Matches(n.Operand, doc),
        FilterQuery f => FilterMatches(f.Filter, doc.Source),
        _ => TermFrequency(node, doc) > 0,
    };

    private bool FilterMatches(Filter filter, ReferenceDocument doc)
    {
        switch (filter)
        {
            case ExtensionFilter e:
                return string.Equals(System.IO.Path.GetExtension(doc.Path).TrimStart('.'), e.Extension, StringComparison.OrdinalIgnoreCase)
                    && System.IO.Path.GetExtension(doc.Path).Length > 0;
            case PathFilter p:
                return doc.Path.Replace('\\', '/').Contains(p.Fragment.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
            case SizeFilter s:
                return s.Comparison == SizeComparison.LessThan ? doc.Size < s.Bytes : doc.Size > s.Bytes;
            case ModifiedFilter m:
            {
                var localDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(doc.LastWriteUtc, DateTimeKind.Utc), _timeZone).Date;
                var date = m.Date.ToDateTime(TimeOnly.MinValue);
                return m.Comparison switch
                {
                    DateComparison.Equal => localDate == date,
                    DateComparison.AtOrAfter => localDate >= date,
                    _ => localDate <= date,
                };
            }

            default:
                throw new InvalidOperationException(filter.GetType().Name);
        }
    }

    // ---------------- scoring ----------------

    public sealed record RefHit(string Path, double Score);

    public (int Total, List<RefHit> Hits) Search(BoundNode query, int topK)
    {
        var n = _docs.Count;
        var avgdl = n == 0 ? 0.0 : _docs.Sum(d => (long)d.Length) / (double)n;

        var positive = new SortedDictionary<string, BoundNode>(StringComparer.Ordinal);
        CollectPositive(query, false, positive);
        var idf = positive.ToDictionary(
            p => p.Key,
            p =>
            {
                var df = _docs.Count(d => TermFrequency(p.Value, d) > 0);
                return Math.Log(1.0 + ((n - df + 0.5) / (df + 0.5)));
            });

        var hits = new List<RefHit>();
        foreach (var doc in _docs.Where(d => Matches(query, d)))
        {
            var score = 0.0;
            foreach (var (key, leaf) in positive)
            {
                var tf = TermFrequency(leaf, doc);
                if (tf == 0)
                {
                    continue;
                }

                var norm = K1 * (1 - B + (B * doc.Length / avgdl));
                score += idf[key] * tf * (K1 + 1) / (tf + norm);
            }

            hits.Add(new RefHit(doc.Source.Path, score));
        }

        var ordered = hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToList();
        return (hits.Count, ordered);
    }

    public int DocumentFrequency(BoundNode leaf) => _docs.Count(d => TermFrequency(leaf, d) > 0);

    private static void CollectPositive(BoundNode node, bool negated, SortedDictionary<string, BoundNode> into)
    {
        switch (node)
        {
            case NotQuery n:
                CollectPositive(n.Operand, !negated, into);
                break;
            case AndQuery a:
                foreach (var c in a.Children)
                {
                    CollectPositive(c, negated, into);
                }

                break;
            case OrQuery o:
                foreach (var c in o.Children)
                {
                    CollectPositive(c, negated, into);
                }

                break;
            case FilterQuery:
                break;
            default:
                if (!negated)
                {
                    into.TryAdd(node.ToString(), node);
                }

                break;
        }
    }
}
