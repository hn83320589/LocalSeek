namespace LocalSeek.Core.Text;

public enum TokenKind : byte
{
    Latin,
    CjkBigram,
    CjkSingle,
}

/// <summary>A token with its position and its [SourceStart, SourceEnd) range in the original text.</summary>
public readonly record struct Token(string Text, TokenKind Kind, int Position, int SourceStart, int SourceEnd);

/// <summary>One occurrence of a CJK ideograph; counted as "single-char term frequency".</summary>
public readonly record struct CjkCharHit(string Char, int SourceStart, int SourceEnd);

public sealed record TokenizedText(IReadOnlyList<Token> Tokens, IReadOnlyList<CjkCharHit> CjkChars)
{
    /// <summary>Field length used for scoring: the number of tokens.</summary>
    public int Length => Tokens.Count;
}

/// <summary>Receives tokens without allocating a string per token. Spans are only valid during the call.</summary>
public interface ITokenSink
{
    void OnToken(ReadOnlySpan<char> text, TokenKind kind, int position, int sourceStart, int sourceEnd);

    void OnCjkChar(ReadOnlySpan<char> ideograph, int sourceStart, int sourceEnd);
}
