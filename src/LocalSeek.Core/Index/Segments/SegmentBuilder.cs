using System.Collections;
using System.Runtime.InteropServices;
using LocalSeek.Core.Storage;

namespace LocalSeek.Core.Index.Segments;

/// <summary>
/// RAM buffer for one segment being built: contents with body postings and docs with name postings.
/// Docs may reference contents of other segments (renames). Written out with <see cref="WriteTo"/>.
/// </summary>
internal sealed class SegmentBuilder(long segmentId)
{
    private const int BytesPerPosting = 12;
    private const int BytesPerTerm = 64;

    private readonly Dictionary<string, Accumulator>[] _spaces = [new(), new(), new(), new()];
    private readonly List<ContentEntry> _contents = [];
    private readonly List<DocEntry> _docs = [];
    private readonly BitArray _deleted = new(0);

    public long SegmentId { get; } = segmentId;

    public IReadOnlyList<ContentEntry> Contents => _contents;

    public IReadOnlyList<DocEntry> Docs => _docs;

    public long EstimatedBytes { get; private set; }

    public int DeletedCount { get; private set; }

    public bool IsDeleted(int docId) => docId < _deleted.Length && _deleted[docId];

    public BitArray CopyDeleted()
    {
        var copy = new BitArray(_docs.Count);
        for (var i = 0; i < _docs.Count; i++)
        {
            copy[i] = IsDeleted(i);
        }

        return copy;
    }

    public void Delete(int docId)
    {
        if (_deleted.Length < _docs.Count)
        {
            _deleted.Length = _docs.Count;
        }

        if (!_deleted[docId])
        {
            _deleted[docId] = true;
            DeletedCount++;
        }
    }

    public int AddContent(AnalyzedField body, ulong contentHash)
    {
        var id = _contents.Count;
        _contents.Add(new ContentEntry(body.Length, contentHash));
        AddField(id, body, TermSpace.BodyTerm, TermSpace.BodyChar);
        return id;
    }

    public int AddDoc(string path, long size, DateTime lastWriteUtc, AnalyzedField name, long contentSegment, int contentId)
    {
        var id = _docs.Count;
        _docs.Add(new DocEntry(path, size, lastWriteUtc.ToUniversalTime().Ticks, name.Length, contentSegment, contentId));
        EstimatedBytes += (path.Length * 2) + 64;
        AddField(id, name, TermSpace.NameTerm, TermSpace.NameChar);
        return id;
    }

    /// <summary>Re-points docs whose content moved (a merge committed while they were buffered).</summary>
    public void RemapContents(long fromSegment, int[] contentMap, long toSegment)
    {
        for (var i = 0; i < _docs.Count; i++)
        {
            var doc = _docs[i];
            if (doc.ContentSegment == fromSegment)
            {
                var mapped = contentMap[doc.ContentId];
                if (mapped < 0)
                {
                    throw new InvalidOperationException($"Buffered document {doc.Path} references a content dropped by a merge.");
                }

                _docs[i] = doc with { ContentSegment = toSegment, ContentId = mapped };
            }
        }
    }

    public void WriteTo(IIndexOutput output)
    {
        var writer = new SegmentWriter(output, SegmentId, _contents, _docs);
        var encoder = new PostingsEncoder();
        var keys = new List<(byte[] Key, Accumulator Postings)>();
        for (var space = 0; space < _spaces.Length; space++)
        {
            foreach (var (term, acc) in _spaces[space])
            {
                keys.Add((SegmentFormat.Key((TermSpace)space, term), acc));
            }
        }

        keys.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        foreach (var (key, acc) in keys)
        {
            encoder.Reset();
            var positionIndex = 0;
            for (var i = 0; i < acc.Ids.Count; i++)
            {
                var frequency = acc.Frequencies[i];
                var positions = acc.HasPositions
                    ? CollectionsMarshal.AsSpan(acc.Positions).Slice(positionIndex, frequency)
                    : [];
                encoder.Add(acc.Ids[i], frequency, positions);
                positionIndex += acc.HasPositions ? frequency : 0;
            }

            writer.AddTerm(key, encoder.Count, encoder.Postings, encoder.Positions);
        }

        writer.Finish();
    }

    private void AddField(int id, AnalyzedField field, TermSpace termSpace, TermSpace charSpace)
    {
        foreach (var (term, positions) in field.Terms)
        {
            var acc = GetAccumulator(termSpace, term, hasPositions: true);
            acc.Ids.Add(id);
            acc.Frequencies.Add(positions.Count);
            acc.Positions.AddRange(positions);
            EstimatedBytes += BytesPerPosting + (4 * positions.Count);
        }

        foreach (var (ideograph, count) in field.Chars)
        {
            var acc = GetAccumulator(charSpace, ideograph, hasPositions: false);
            acc.Ids.Add(id);
            acc.Frequencies.Add(count);
            EstimatedBytes += BytesPerPosting;
        }
    }

    private Accumulator GetAccumulator(TermSpace space, string term, bool hasPositions)
    {
        ref var acc = ref CollectionsMarshal.GetValueRefOrAddDefault(_spaces[(int)space], term, out var exists);
        if (!exists)
        {
            acc = new Accumulator(hasPositions);
            EstimatedBytes += BytesPerTerm + (term.Length * 2);
        }

        return acc!;
    }

    private sealed class Accumulator(bool hasPositions)
    {
        public bool HasPositions { get; } = hasPositions;

        public List<int> Ids { get; } = [];

        public List<int> Frequencies { get; } = [];

        public List<int> Positions { get; } = [];
    }
}
