namespace LocalSeek.Core.Text;

/// <summary>
/// Splits normalized text into Latin tokens ([a-z0-9]+), overlapping CJK bigrams (runs of length ≥ 2)
/// and CJK singles (runs of length 1). Every other char is a separator and does not consume a position.
/// An instance reuses its buffers and is not thread-safe; use one per worker.
/// </summary>
public sealed class Tokenizer
{
    private readonly NormalizedText _normalized = new();

    public static bool IsCjk(int codePoint) =>
        codePoint is (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0x20000 and <= 0x2EBEF);

    public static bool IsLatin(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');

    public static TokenizedText TokenizeToList(string text)
    {
        var sink = new ListSink();
        new Tokenizer().Tokenize(text, ref sink);
        return new TokenizedText(sink.Tokens, sink.Chars);
    }

    /// <returns>The number of tokens emitted (the field length).</returns>
    public int Tokenize<TSink>(ReadOnlySpan<char> text, ref TSink sink)
        where TSink : ITokenSink, allows ref struct
    {
        TextNormalizer.Normalize(text, _normalized);
        var n = _normalized;
        var chars = n.Chars;
        var position = 0;
        var i = 0;
        while (i < chars.Length)
        {
            if (IsLatin(chars[i]))
            {
                var start = i;
                while (i < chars.Length && IsLatin(chars[i]))
                {
                    i++;
                }

                sink.OnToken(chars[start..i], TokenKind.Latin, position++, n.SourceStart(start), n.SourceEnd(i - 1));
                continue;
            }

            var length = TextNormalizer.CodePointLength(chars, i);
            if (!IsCjkAt(chars, i, length))
            {
                i += length;
                continue;
            }

            var runStart = i;
            var count = 0;
            var previous = -1;
            while (i < chars.Length)
            {
                length = TextNormalizer.CodePointLength(chars, i);
                if (!IsCjkAt(chars, i, length))
                {
                    break;
                }

                sink.OnCjkChar(chars.Slice(i, length), n.SourceStart(i), n.SourceEnd(i));
                if (previous >= 0)
                {
                    sink.OnToken(chars[previous..(i + length)], TokenKind.CjkBigram, position + count - 1, n.SourceStart(previous), n.SourceEnd(i));
                }

                previous = i;
                count++;
                i += length;
            }

            if (count == 1)
            {
                sink.OnToken(chars[runStart..i], TokenKind.CjkSingle, position, n.SourceStart(runStart), n.SourceEnd(runStart));
                position++;
            }
            else
            {
                position += count - 1;
            }
        }

        return position;
    }

    private static bool IsCjkAt(ReadOnlySpan<char> chars, int index, int length) =>
        IsCjk(length == 2 ? char.ConvertToUtf32(chars[index], chars[index + 1]) : chars[index]);

    private sealed class ListSink : ITokenSink
    {
        public List<Token> Tokens { get; } = [];

        public List<CjkCharHit> Chars { get; } = [];

        public void OnToken(ReadOnlySpan<char> text, TokenKind kind, int position, int sourceStart, int sourceEnd) =>
            Tokens.Add(new Token(new string(text), kind, position, sourceStart, sourceEnd));

        public void OnCjkChar(ReadOnlySpan<char> ideograph, int sourceStart, int sourceEnd) =>
            Chars.Add(new CjkCharHit(new string(ideograph), sourceStart, sourceEnd));
    }
}
