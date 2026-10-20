using LocalSeek.Core.Query;

namespace LocalSeek.Core.Index;

/// <summary>A document to be indexed: metadata plus the extracted body text.</summary>
public sealed record DocumentInput(string Path, long Size, DateTime LastWriteUtc, string Body, ulong ContentHash = 0);

/// <summary>
/// Simple in-memory implementation of <see cref="IIndexReader"/>. Used directly in tests and as the
/// specification the on-disk segment reader must agree with.
/// </summary>
public sealed class MemoryIndex : IIndexReader
{
    private readonly List<(DocumentMetadata Metadata, int Length)> _docs = [];
    private readonly Dictionary<string, List<(int Doc, List<int> Positions)>>[] _positional = [new(), new()];
    private readonly Dictionary<string, List<(int Doc, int Frequency)>>[] _chars = [new(), new()];
    private readonly DocumentAnalyzer _analyzer = new();
    private readonly string[]?[] _sortedTerms = new string[]?[4];

    public int DocCount => _docs.Count;

    public long TotalLength { get; private set; }

    public static MemoryIndex Build(IEnumerable<DocumentInput> documents)
    {
        var index = new MemoryIndex();
        foreach (var d in documents)
        {
            index.Add(d);
        }

        return index;
    }

    public void Add(DocumentInput input)
    {
        var doc = _docs.Count;
        var body = _analyzer.Analyze(input.Body);
        var name = _analyzer.Analyze(DocumentAnalyzer.FileNameOf(input.Path));
        AddField(doc, body, 0);
        AddField(doc, name, 1);

        var length = body.Length + (2 * name.Length);
        _docs.Add((new DocumentMetadata(input.Path, input.Size, input.LastWriteUtc), length));
        TotalLength += length;
        Array.Clear(_sortedTerms);
    }

    public int GetLength(int doc) => _docs[doc].Length;

    public DocumentMetadata GetMetadata(int doc) => _docs[doc].Metadata;

    public void ForEachPosting(TermSpace space, string term, PostingCallback callback)
    {
        if (IsPositional(space, out var field))
        {
            if (_positional[field].TryGetValue(term, out var list))
            {
                foreach (var (doc, positions) in list)
                {
                    callback(doc, positions.Count);
                }
            }
        }
        else if (_chars[field].TryGetValue(term, out var list))
        {
            foreach (var (doc, frequency) in list)
            {
                callback(doc, frequency);
            }
        }
    }

    public void ForEachPositions(TermSpace space, string term, PositionsCallback callback)
    {
        if (!IsPositional(space, out var field))
        {
            throw new ArgumentException($"{space} has no positions.", nameof(space));
        }

        if (_positional[field].TryGetValue(term, out var list))
        {
            foreach (var (doc, positions) in list)
            {
                callback(doc, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(positions));
            }
        }
    }

    public IReadOnlyList<string> GetTermsWithPrefix(TermSpace space, string prefix)
    {
        var sorted = _sortedTerms[(int)space] ??= SortedTerms(space);
        var start = LowerBound(sorted, prefix);
        var end = start;
        while (end < sorted.Length && sorted[end].StartsWith(prefix, StringComparison.Ordinal))
        {
            end++;
        }

        return sorted[start..end];
    }

    private string[] SortedTerms(TermSpace space)
    {
        IEnumerable<string> keys = IsPositional(space, out var field) ? _positional[field].Keys : _chars[field].Keys;
        var sorted = keys.ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);
        return sorted;
    }

    private static int LowerBound(string[] sorted, string value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(sorted[mid], value) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static bool IsPositional(TermSpace space, out int field)
    {
        field = space is TermSpace.BodyTerm or TermSpace.BodyChar ? 0 : 1;
        return space is TermSpace.BodyTerm or TermSpace.NameTerm;
    }

    private void AddField(int doc, AnalyzedField field, int fieldIndex)
    {
        foreach (var (term, positions) in field.Terms)
        {
            if (!_positional[fieldIndex].TryGetValue(term, out var list))
            {
                list = [];
                _positional[fieldIndex][term] = list;
            }

            list.Add((doc, positions));
        }

        foreach (var (ideograph, count) in field.Chars)
        {
            if (!_chars[fieldIndex].TryGetValue(ideograph, out var list))
            {
                list = [];
                _chars[fieldIndex][ideograph] = list;
            }

            list.Add((doc, count));
        }
    }
}
