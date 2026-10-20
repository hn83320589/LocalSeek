using System.Collections;
using LocalSeek.Core.Index.Segments;
using LocalSeek.Core.Query;

namespace LocalSeek.Core.Index;

/// <summary>
/// An immutable, reference-counted view of one commit. Live documents get dense ordinals; body
/// postings (keyed by content) are mapped to the live document that owns the content. Commits and
/// merges that happen after the snapshot was acquired never change what it returns, and its files
/// are not deleted until it is disposed.
/// </summary>
public sealed class IndexSnapshot : IIndexReader, IDisposable
{
    private readonly View[] _views;
    private readonly DocumentMetadata[] _metadata;
    private readonly int[] _lengths;
    private readonly Action<IndexSnapshot> _release;
    private int _refCount = 1;

    internal IndexSnapshot(long generation, IReadOnlyList<(SegmentReader Reader, BitArray? Deleted)> segments, IReadOnlyList<string> files, Action<IndexSnapshot> release)
    {
        Generation = generation;
        Files = files;
        _release = release;
        _views = segments.Select(s => new View(s.Reader, s.Deleted)).ToArray();
        var viewById = _views.Select((v, i) => (v.Reader.SegmentId, i)).ToDictionary(x => x.SegmentId, x => x.i);

        var metadata = new List<DocumentMetadata>();
        var lengths = new List<int>();
        long total = 0;
        foreach (var view in _views)
        {
            var docs = view.Reader.Docs;
            for (var d = 0; d < docs.Count; d++)
            {
                if (view.Deleted is not null && view.Deleted[d])
                {
                    continue;
                }

                var doc = docs[d];
                if (!viewById.TryGetValue(doc.ContentSegment, out var target))
                {
                    throw new IndexCorruptException($"文件 {doc.Path} 參照不存在的片段 {doc.ContentSegment}");
                }

                var ordinal = metadata.Count;
                view.DocOrdinal[d] = ordinal;
                _views[target].ContentOwner[doc.ContentId] = ordinal;
                var length = _views[target].Reader.Contents[doc.ContentId].BodyLength + (2 * doc.NameLength);
                metadata.Add(new DocumentMetadata(doc.Path, doc.Size, doc.LastWriteUtc));
                lengths.Add(length);
                total += length;
            }
        }

        _metadata = [.. metadata];
        _lengths = [.. lengths];
        TotalLength = total;
    }

    public long Generation { get; }

    /// <summary>Index files this snapshot depends on.</summary>
    public IReadOnlyList<string> Files { get; }

    public int SegmentCount => _views.Length;

    public int DocCount => _metadata.Length;

    public long TotalLength { get; }

    public int GetLength(int doc) => _lengths[doc];

    public DocumentMetadata GetMetadata(int doc) => _metadata[doc];

    public void ForEachPosting(TermSpace space, string term, PostingCallback callback)
    {
        var key = SegmentFormat.Key(space, term);
        foreach (var view in _views)
        {
            if (!view.Reader.TryGetTerm(key, out var entry))
            {
                continue;
            }

            var map = view.MapFor(space);
            PostingsDecoder.Read(view.Reader.ReadPostings(entry), (id, frequency) =>
            {
                var ordinal = map[id];
                if (ordinal >= 0)
                {
                    callback(ordinal, frequency);
                }
            });
        }
    }

    public void ForEachPositions(TermSpace space, string term, PositionsCallback callback, Func<int, bool>? wanted = null)
    {
        if (space is not (TermSpace.BodyTerm or TermSpace.NameTerm))
        {
            throw new ArgumentException($"{space} has no positions.", nameof(space));
        }

        var key = SegmentFormat.Key(space, term);
        foreach (var view in _views)
        {
            if (!view.Reader.TryGetTerm(key, out var entry))
            {
                continue;
            }

            var map = view.MapFor(space);
            var bytes = view.Reader.ReadPostingsAndPositions(entry);
            PostingsDecoder.ReadWithPositions(
                bytes.AsSpan(0, entry.PostingsLength),
                bytes.AsSpan(entry.PostingsLength),
                (id, positions) => callback(map[id], positions),
                id => map[id] >= 0 && (wanted is null || wanted(map[id])));
        }
    }

    public IReadOnlyList<string> GetTermsWithPrefix(TermSpace space, string prefix)
    {
        var key = SegmentFormat.Key(space, prefix);
        var terms = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var view in _views)
        {
            foreach (var entry in view.Reader.TermsWithPrefix(key))
            {
                terms.Add(SegmentFormat.TermOf(entry.Key));
            }
        }

        return [.. terms];
    }

    /// <summary>Adds a reference; each call must be paired with <see cref="Dispose"/>.</summary>
    internal bool TryAddRef()
    {
        while (true)
        {
            var current = Volatile.Read(ref _refCount);
            if (current == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public void Dispose()
    {
        var remaining = Interlocked.Decrement(ref _refCount);
        if (remaining == 0)
        {
            _release(this);
        }
        else if (remaining < 0)
        {
            throw new ObjectDisposedException(nameof(IndexSnapshot), "Snapshot released more times than acquired.");
        }
    }

    private sealed class View
    {
        public View(SegmentReader reader, BitArray? deleted)
        {
            Reader = reader;
            Deleted = deleted;
            DocOrdinal = new int[reader.Docs.Count];
            ContentOwner = new int[reader.Contents.Count];
            Array.Fill(DocOrdinal, -1);
            Array.Fill(ContentOwner, -1);
        }

        public SegmentReader Reader { get; }

        public BitArray? Deleted { get; }

        public int[] DocOrdinal { get; }

        public int[] ContentOwner { get; }

        public int[] MapFor(TermSpace space) =>
            space is TermSpace.BodyTerm or TermSpace.BodyChar ? ContentOwner : DocOrdinal;
    }
}
