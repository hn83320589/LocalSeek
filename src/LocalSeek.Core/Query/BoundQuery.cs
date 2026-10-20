namespace LocalSeek.Core.Query;

/// <summary>
/// Query after tokenization. ToString() is a canonical form, also used as the identity of a
/// scoring term so that repeated identical terms are scored once.
/// </summary>
public abstract record BoundNode;

/// <summary>A single Latin or CJK-bigram term.</summary>
public sealed record TermQuery(string Term) : BoundNode
{
    public override string ToString() => $"term({Term})";
}

/// <summary>A single CJK ideograph, scored with the single-char frequency.</summary>
public sealed record CharQuery(string Char) : BoundNode
{
    public override string ToString() => $"char({Char})";
}

/// <summary>Terms that must occur at consecutive positions in the same field.</summary>
public sealed record PhraseQuery(IReadOnlyList<string> Terms) : BoundNode
{
    public override string ToString() => $"phrase({string.Join(' ', Terms)})";
}

/// <summary>All Latin terms starting with Prefix (at least 2 chars).</summary>
public sealed record PrefixQuery(string Prefix) : BoundNode
{
    public override string ToString() => $"prefix({Prefix})";
}

public sealed record FilterQuery(Filter Filter) : BoundNode
{
    public override string ToString() => Filter.ToString();
}

public sealed record NotQuery(BoundNode Operand) : BoundNode
{
    public override string ToString() => $"(NOT {Operand})";
}

public sealed record AndQuery(IReadOnlyList<BoundNode> Children) : BoundNode
{
    public override string ToString() => $"(AND {string.Join(' ', Children)})";
}

public sealed record OrQuery(IReadOnlyList<BoundNode> Children) : BoundNode
{
    public override string ToString() => $"(OR {string.Join(' ', Children)})";
}
