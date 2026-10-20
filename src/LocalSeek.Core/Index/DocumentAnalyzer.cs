using LocalSeek.Core.Text;

namespace LocalSeek.Core.Index;

/// <summary>Postings of one field of one document.</summary>
public sealed class AnalyzedField
{
    public static readonly AnalyzedField Empty = new(0, [], []);

    public AnalyzedField(int length, Dictionary<string, List<int>> terms, Dictionary<string, int> chars)
    {
        Length = length;
        Terms = terms;
        Chars = chars;
    }

    /// <summary>Token count, CJK singles included.</summary>
    public int Length { get; }

    /// <summary>Latin and bigram terms → ascending positions. CJK singles are not stored: queries use <see cref="Chars"/>.</summary>
    public Dictionary<string, List<int>> Terms { get; }

    /// <summary>CJK ideograph → occurrence count.</summary>
    public Dictionary<string, int> Chars { get; }
}

/// <summary>Tokenizes a field into postings. Reuses buffers; one instance per worker thread.</summary>
public sealed class DocumentAnalyzer
{
    private readonly Tokenizer _tokenizer = new();

    /// <summary>Number of body/name fields analyzed; lets tests assert that renames skip tokenization.</summary>
    public long AnalyzedFieldCount { get; private set; }

    public static string FileNameOf(string path)
    {
        var slash = path.AsSpan().LastIndexOfAny('\\', '/');
        return path[(slash + 1)..];
    }

    public AnalyzedField Analyze(ReadOnlySpan<char> text)
    {
        AnalyzedFieldCount++;
        var terms = new Dictionary<string, List<int>>();
        var chars = new Dictionary<string, int>();
        var sink = new Sink(terms, chars);
        var length = _tokenizer.Tokenize(text, ref sink);
        return new AnalyzedField(length, terms, chars);
    }

    private readonly struct Sink(Dictionary<string, List<int>> terms, Dictionary<string, int> chars) : ITokenSink
    {
        private readonly Dictionary<string, List<int>>.AlternateLookup<ReadOnlySpan<char>> _terms = terms.GetAlternateLookup<ReadOnlySpan<char>>();
        private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _chars = chars.GetAlternateLookup<ReadOnlySpan<char>>();

        public void OnToken(ReadOnlySpan<char> text, TokenKind kind, int position, int sourceStart, int sourceEnd)
        {
            if (kind == TokenKind.CjkSingle)
            {
                return;
            }

            if (!_terms.TryGetValue(text, out var positions))
            {
                positions = [];
                _terms[text] = positions;
            }

            positions.Add(position);
        }

        public void OnCjkChar(ReadOnlySpan<char> ideograph, int sourceStart, int sourceEnd)
        {
            _chars.TryGetValue(ideograph, out var count);
            _chars[ideograph] = count + 1;
        }
    }
}
