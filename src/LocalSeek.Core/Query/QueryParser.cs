namespace LocalSeek.Core.Query;

/// <summary>
/// Recursive-descent parser. Precedence: NOT (or leading '-') &gt; AND (explicit or implicit) &gt; OR.
/// <code>
/// or      := and ( "OR" and )*
/// and     := unary ( ["AND"] unary )*
/// unary   := ("NOT" | "-") unary | primary
/// primary := "(" or ")" | PHRASE | FILTER | WORD | PREFIX
/// </code>
/// </summary>
public sealed class QueryParser
{
    private readonly List<LexToken> _tokens;
    private int _index;

    private QueryParser(List<LexToken> tokens) => _tokens = tokens;

    public static QueryNode Parse(string query)
    {
        var parser = new QueryParser(QueryLexer.Lex(query));
        if (parser.Peek.Kind == LexKind.End)
        {
            throw new QuerySyntaxException("查詢是空的", 0, query.Length);
        }

        var node = parser.ParseOr();
        var rest = parser.Peek;
        if (rest.Kind != LexKind.End)
        {
            throw rest.Kind == LexKind.RParen
                ? new QuerySyntaxException("多餘的右括號", rest.Start, rest.Length)
                : new QuerySyntaxException("無法解析的內容", rest.Start, rest.Length);
        }

        return node;
    }

    private LexToken Peek => _tokens[_index];

    private LexToken Next() => _tokens[_index++];

    private static bool StartsOperand(LexKind kind) =>
        kind is LexKind.LParen or LexKind.Not or LexKind.Word or LexKind.Prefix or LexKind.Phrase or LexKind.Filter;

    private QueryNode ParseOr()
    {
        var children = new List<QueryNode> { ParseAnd() };
        while (Peek.Kind == LexKind.Or)
        {
            var op = Next();
            RequireOperandAfter(op, "OR");
            children.Add(ParseAnd());
        }

        return children.Count == 1 ? children[0] : new OrNode(children, children[0].Start, End(children[^1]) - children[0].Start);
    }

    private QueryNode ParseAnd()
    {
        var children = new List<QueryNode> { ParseUnary() };
        while (true)
        {
            if (Peek.Kind == LexKind.And)
            {
                var op = Next();
                RequireOperandAfter(op, "AND");
            }
            else if (!StartsOperand(Peek.Kind))
            {
                break;
            }

            children.Add(ParseUnary());
        }

        return children.Count == 1 ? children[0] : new AndNode(children, children[0].Start, End(children[^1]) - children[0].Start);
    }

    private QueryNode ParseUnary()
    {
        if (Peek.Kind != LexKind.Not)
        {
            return ParsePrimary();
        }

        var op = Next();
        RequireOperandAfter(op, "NOT");
        var operand = ParseUnary();
        return new NotNode(operand, op.Start, End(operand) - op.Start);
    }

    private QueryNode ParsePrimary()
    {
        var token = Next();
        switch (token.Kind)
        {
            case LexKind.Word:
                return new WordNode(token.Text, token.Start, token.Length);
            case LexKind.Prefix:
                return new PrefixNode(token.Text, token.Start, token.Length);
            case LexKind.Phrase:
                return new PhraseNode(token.Text, token.Start, token.Length);
            case LexKind.Filter:
                return new FilterNode(token.Filter!, token.Start, token.Length);
            case LexKind.LParen:
                if (Peek.Kind == LexKind.RParen)
                {
                    throw new QuerySyntaxException("括號內沒有內容", token.Start, Peek.Start + 1 - token.Start);
                }

                var inner = ParseOr();
                if (Peek.Kind != LexKind.RParen)
                {
                    throw new QuerySyntaxException("缺少右括號", token.Start, 1);
                }

                Next();
                return inner;
            case LexKind.And:
                throw new QuerySyntaxException("AND 前面缺少條件", token.Start, token.Length);
            case LexKind.Or:
                throw new QuerySyntaxException("OR 前面缺少條件", token.Start, token.Length);
            case LexKind.RParen:
                throw new QuerySyntaxException("多餘的右括號", token.Start, token.Length);
            default:
                throw new QuerySyntaxException("查詢不完整", token.Start, token.Length);
        }
    }

    private void RequireOperandAfter(LexToken op, string name)
    {
        if (!StartsOperand(Peek.Kind))
        {
            throw new QuerySyntaxException($"{name} 後面缺少條件", op.Start, op.Length);
        }
    }

    private static int End(QueryNode node) => node.Start + node.Length;
}
