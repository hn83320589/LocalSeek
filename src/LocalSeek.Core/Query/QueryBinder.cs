using LocalSeek.Core.Text;

namespace LocalSeek.Core.Query;

/// <summary>
/// Turns the raw syntax tree into term queries using the engine tokenizer:
/// <list type="bullet">
/// <item>word → one token: term (or char for a single CJK ideograph); several tokens without singles:
/// phrase; several tokens with a single: AND of the tokens; no tokens: ignored.</item>
/// <item>"phrase" → tokens must not contain singles.</item>
/// <item>prefix → exactly one Latin token of at least 2 chars.</item>
/// </list>
/// The bound query must be "bounded": it cannot consist only of negative conditions.
/// </summary>
public static class QueryBinder
{
    public static BoundNode Bind(QueryNode root)
    {
        var bound = BindNode(root)
            ?? throw new QuerySyntaxException("查詢沒有可用的詞項或篩選條件", root.Start, root.Length);
        if (!IsBounded(bound))
        {
            throw new QuerySyntaxException("查詢不可只有排除條件（NOT）", root.Start, root.Length);
        }

        return bound;
    }

    private static BoundNode? BindNode(QueryNode node) => node switch
    {
        WordNode w => BindWord(w),
        PhraseNode p => BindPhrase(p),
        PrefixNode p => BindPrefix(p),
        FilterNode f => new FilterQuery(f.Filter),
        NotNode n => BindNode(n.Operand) is { } operand ? new NotQuery(operand) : null,
        AndNode a => Combine(a.Children, children => new AndQuery(children)),
        OrNode o => Combine(o.Children, children => new OrQuery(children)),
        _ => throw new InvalidOperationException($"Unknown node {node.GetType().Name}."),
    };

    private static BoundNode? Combine(IReadOnlyList<QueryNode> nodes, Func<List<BoundNode>, BoundNode> create)
    {
        var children = nodes.Select(BindNode).OfType<BoundNode>().ToList();
        return children.Count switch
        {
            0 => null,
            1 => children[0],
            _ => create(children),
        };
    }

    private static BoundNode? BindWord(WordNode word)
    {
        var tokens = Tokenizer.TokenizeToList(word.Text).Tokens;
        if (tokens.Count == 0)
        {
            return null;
        }

        if (tokens.Count == 1)
        {
            return Single(tokens[0]);
        }

        return tokens.Any(t => t.Kind == TokenKind.CjkSingle)
            ? new AndQuery(tokens.Select(Single).ToList())
            : new PhraseQuery(tokens.Select(t => t.Text).ToList());
    }

    private static BoundNode? BindPhrase(PhraseNode phrase)
    {
        var tokens = Tokenizer.TokenizeToList(phrase.Text).Tokens;
        if (tokens.Any(t => t.Kind == TokenKind.CjkSingle))
        {
            throw new QuerySyntaxException("片語不可包含單字詞元", phrase.Start, phrase.Length);
        }

        return tokens.Count switch
        {
            0 => null,
            1 => new TermQuery(tokens[0].Text),
            _ => new PhraseQuery(tokens.Select(t => t.Text).ToList()),
        };
    }

    private static PrefixQuery BindPrefix(PrefixNode prefix)
    {
        var tokens = Tokenizer.TokenizeToList(prefix.Text).Tokens;
        if (tokens.Count != 1 || tokens[0].Kind != TokenKind.Latin || tokens[0].Text.Length < 2)
        {
            throw new QuerySyntaxException("前綴至少需要 2 個 Latin 字元", prefix.Start, prefix.Length);
        }

        return new PrefixQuery(tokens[0].Text);
    }

    private static BoundNode Single(Token token) =>
        token.Kind == TokenKind.CjkSingle ? new CharQuery(token.Text) : new TermQuery(token.Text);

    /// <summary>Whether the matching set is limited by at least one positive condition.</summary>
    public static bool IsBounded(BoundNode node) => node switch
    {
        NotQuery n => IsComplementBounded(n.Operand),
        AndQuery a => a.Children.Any(IsBounded),
        OrQuery o => o.Children.All(IsBounded),
        _ => true,
    };

    /// <summary>Whether NOT node would be bounded (De Morgan dual of <see cref="IsBounded"/>).</summary>
    private static bool IsComplementBounded(BoundNode node) => node switch
    {
        NotQuery n => IsBounded(n.Operand),
        AndQuery a => a.Children.All(IsComplementBounded),
        OrQuery o => o.Children.Any(IsComplementBounded),
        _ => false,
    };
}
