namespace LocalSeek.Core.Query;

/// <summary>
/// Parsed query before tokenization. Leaves keep the raw text typed by the user so that
/// different binders (the engine's and the test reference's) can apply their own tokenizer.
/// </summary>
public abstract record QueryNode(int Start, int Length);

public sealed record WordNode(string Text, int Start, int Length) : QueryNode(Start, Length)
{
    public override string ToString() => $"word({Text})";
}

/// <summary>abc* — Text excludes the trailing '*'.</summary>
public sealed record PrefixNode(string Text, int Start, int Length) : QueryNode(Start, Length)
{
    public override string ToString() => $"prefix({Text})";
}

/// <summary>"..." — Text excludes the quotes.</summary>
public sealed record PhraseNode(string Text, int Start, int Length) : QueryNode(Start, Length)
{
    public override string ToString() => $"phrase({Text})";
}

public sealed record FilterNode(Filter Filter, int Start, int Length) : QueryNode(Start, Length)
{
    public override string ToString() => Filter.ToString();
}

public sealed record NotNode(QueryNode Operand, int Start, int Length) : QueryNode(Start, Length)
{
    public override string ToString() => $"(NOT {Operand})";
}

public sealed record AndNode(IReadOnlyList<QueryNode> Children, int Start, int Length) : QueryNode(Start, Length)
{
    public override string ToString() => $"(AND {string.Join(' ', Children)})";
}

public sealed record OrNode(IReadOnlyList<QueryNode> Children, int Start, int Length) : QueryNode(Start, Length)
{
    public override string ToString() => $"(OR {string.Join(' ', Children)})";
}
