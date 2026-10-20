using LocalSeek.Core.Query;

namespace LocalSeek.Core.Index;

/// <summary>The four kinds of postings a document contributes.</summary>
public enum TermSpace : byte
{
    /// <summary>Body tokens (Latin and CJK bigrams), with positions.</summary>
    BodyTerm,

    /// <summary>File-name tokens, with positions; weighted ×2.</summary>
    NameTerm,

    /// <summary>Body single-ideograph frequencies, no positions.</summary>
    BodyChar,

    /// <summary>File-name single-ideograph frequencies, no positions; weighted ×2.</summary>
    NameChar,
}

public delegate void PostingCallback(int doc, int frequency);

public delegate void PositionsCallback(int doc, ReadOnlySpan<int> positions);

/// <summary>
/// A consistent, read-only view of live documents. Documents are addressed by dense ordinals
/// 0..DocCount-1; deleted documents never appear. All counts are exact integers.
/// </summary>
public interface IIndexReader
{
    int DocCount { get; }

    /// <summary>Sum of <see cref="GetLength"/> over all live documents.</summary>
    long TotalLength { get; }

    /// <summary>len(d) = body token count + 2 × name token count.</summary>
    int GetLength(int doc);

    DocumentMetadata GetMetadata(int doc);

    /// <summary>Raw (unweighted) frequency of the term per live document.</summary>
    void ForEachPosting(TermSpace space, string term, PostingCallback callback);

    /// <summary>
    /// Sorted positions per live document; only for <see cref="TermSpace.BodyTerm"/> and <see cref="TermSpace.NameTerm"/>.
    /// When <paramref name="wanted"/> is given, documents it rejects are skipped without decoding their positions.
    /// </summary>
    void ForEachPositions(TermSpace space, string term, PositionsCallback callback, Func<int, bool>? wanted = null);

    /// <summary>Distinct terms in the space starting with the prefix, ordinal order.</summary>
    IReadOnlyList<string> GetTermsWithPrefix(TermSpace space, string prefix);
}
