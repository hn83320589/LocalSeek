using System.Collections;
using LocalSeek.Core.Index.Segments;
using LocalSeek.Core.Storage;

namespace LocalSeek.Core.Index;

/// <summary>
/// The single writer of an index directory. Changes accumulate in a RAM segment and in-memory
/// tombstones and become visible atomically on <see cref="Commit"/>:
/// <list type="number">
/// <item>new segment and tombstone files are written and flushed;</item>
/// <item>manifest_N.tmp is written and flushed;</item>
/// <item>it is atomically renamed to manifest_N.lsm — the commit point.</item>
/// </list>
/// On open the newest valid manifest wins; anything it does not reference is an orphan and removed.
/// If any I/O fails mid-operation the writer refuses further work; reopen to recover.
/// </summary>
public sealed class IndexWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly IIndexStorage _storage;
    private readonly IndexWriterOptions _options;
    private readonly FileReferences _refs;
    private readonly DocumentAnalyzer _analyzer = new();
    private readonly List<SegmentState> _segments = [];
    private readonly Dictionary<long, SegmentState> _byId = [];
    private readonly Dictionary<string, DocLocation> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string[]> _retainedCommits = new();
    private SegmentBuilder _builder = null!;
    private IndexSnapshot _current = null!;
    private long _generation;
    private long _nextSegmentId;
    private bool _dirty;
    private bool _failed;
    private bool _disposed;

    private IndexWriter(IIndexStorage storage, IndexWriterOptions options)
    {
        _storage = storage;
        _options = options;
        _refs = new FileReferences(storage);
    }

    public IndexOpenReport OpenReport { get; private set; } = new([]);

    public long Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    public static IndexWriter Open(IIndexStorage storage, IndexWriterOptions? options = null)
    {
        var writer = new IndexWriter(storage, options ?? new IndexWriterOptions());
        try
        {
            writer.Load();
            return writer;
        }
        catch
        {
            writer._refs.Close();
            throw;
        }
    }

    // ------------------------------------------------------------------ reading

    /// <summary>The latest committed state. Dispose the snapshot when done.</summary>
    public IndexSnapshot AcquireSnapshot()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _current.TryAddRef() ? _current : throw new InvalidOperationException("Current snapshot already released.");
        }
    }

    public bool TryGetEntry(string path, out IndexedEntry entry)
    {
        lock (_lock)
        {
            if (_live.TryGetValue(path, out var location))
            {
                entry = ToEntry(location);
                return true;
            }

            entry = null!;
            return false;
        }
    }

    /// <summary>All live documents, including uncommitted changes.</summary>
    public IReadOnlyList<IndexedEntry> GetEntries()
    {
        lock (_lock)
        {
            return _live.Values.Select(ToEntry).ToList();
        }
    }

    public IndexStatistics GetStatistics()
    {
        lock (_lock)
        {
            return new IndexStatistics(
                _generation,
                _live.Count,
                _segments.Count,
                _segments.Sum(s => s.DeletedCount),
                _segments.Sum(s => s.Reader.FileLength),
                _refs.PendingDeleteCount);
        }
    }

    // ------------------------------------------------------------------ writing

    public void AddOrUpdate(DocumentInput input)
    {
        lock (_lock)
        {
            ThrowIfUnusable();
            AddOrUpdateLocked(PreparedDocument.Create(_analyzer, input));
        }
    }

    public void AddOrUpdate(PreparedDocument document)
    {
        lock (_lock)
        {
            ThrowIfUnusable();
            AddOrUpdateLocked(document);
        }
    }

    public bool Delete(string path)
    {
        lock (_lock)
        {
            ThrowIfUnusable();
            if (!_live.Remove(path, out var location))
            {
                return false;
            }

            Tombstone(location);
            return true;
        }
    }

    /// <summary>
    /// Moves a document whose content did not change: the body postings are reused by reference and
    /// only the file name is tokenized. Returns false if <paramref name="oldPath"/> is not indexed.
    /// </summary>
    public bool Rename(string oldPath, string newPath, long size, DateTime lastWriteUtc)
    {
        lock (_lock)
        {
            ThrowIfUnusable();
            if (!_live.Remove(oldPath, out var oldLocation))
            {
                return false;
            }

            var old = GetDoc(oldLocation);
            Tombstone(oldLocation);
            if (_live.Remove(newPath, out var overwritten))
            {
                Tombstone(overwritten);
            }

            var name = _analyzer.Analyze(DocumentAnalyzer.FileNameOf(newPath));
            var docId = _builder.AddDoc(newPath, size, lastWriteUtc, name, old.ContentSegment, old.ContentId);
            _live[newPath] = new DocLocation(_builder.SegmentId, docId);
            _dirty = true;
            return true;
        }
    }

    /// <summary>Makes all changes durable and visible. Returns the committed generation.</summary>
    public long Commit()
    {
        lock (_lock)
        {
            ThrowIfUnusable();
            try
            {
                return CommitLocked();
            }
            catch
            {
                _failed = true;
                throw;
            }
        }
    }

    /// <summary>
    /// Reads every segment completely to verify its checksums. Damaged segments are dropped (their
    /// documents disappear from the index and must be re-indexed) and the repair is committed.
    /// </summary>
    public IReadOnlyList<IndexIssue> VerifySegments()
    {
        List<SegmentState> segments;
        lock (_lock)
        {
            ThrowIfUnusable();
            segments = [.. _segments];
        }

        var issues = new List<IndexIssue>();
        foreach (var segment in segments)
        {
            try
            {
                segment.Reader.VerifyFully();
            }
            catch (IndexCorruptException ex)
            {
                lock (_lock)
                {
                    var affected = DropSegmentLocked(segment);
                    issues.Add(new IndexIssue(IndexIssueKind.SegmentCorrupt, segment.Reader.FileName, ex.Message, affected));
                }
            }
        }

        if (issues.Count > 0)
        {
            Commit();
        }

        return issues;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _refs.Close();
        }
    }

    // ------------------------------------------------------------------ internals

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed)
        {
            throw new InvalidOperationException("索引寫入器先前發生 I/O 錯誤，必須重新開啟索引。");
        }
    }

    private void AddOrUpdateLocked(PreparedDocument document)
    {
        try
        {
            if (_live.Remove(document.Path, out var old))
            {
                Tombstone(old);
            }

            var contentId = _builder.AddContent(document.Body, document.ContentHash);
            var docId = _builder.AddDoc(document.Path, document.Size, document.LastWriteUtc, document.Name, _builder.SegmentId, contentId);
            _live[document.Path] = new DocLocation(_builder.SegmentId, docId);
            _dirty = true;
            if (_builder.EstimatedBytes >= _options.RamBufferBytes)
            {
                FlushBuilder();
            }
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    private void Tombstone(DocLocation location)
    {
        if (location.SegmentId == _builder.SegmentId)
        {
            _builder.Delete(location.DocId);
        }
        else
        {
            _byId[location.SegmentId].MarkDeleted(location.DocId);
        }

        _dirty = true;
    }

    private DocEntry GetDoc(DocLocation location) =>
        location.SegmentId == _builder.SegmentId ? _builder.Docs[location.DocId] : _byId[location.SegmentId].Reader.Docs[location.DocId];

    private ContentEntry GetContent(long segmentId, int contentId) =>
        segmentId == _builder.SegmentId ? _builder.Contents[contentId] : _byId[segmentId].Reader.Contents[contentId];

    private IndexedEntry ToEntry(DocLocation location)
    {
        var doc = GetDoc(location);
        return new IndexedEntry(doc.Path, doc.Size, doc.LastWriteUtc, GetContent(doc.ContentSegment, doc.ContentId).ContentHash);
    }

    private void FlushBuilder()
    {
        if (_builder.Docs.Count == 0)
        {
            return;
        }

        var name = IndexFileNames.Segment(_builder.SegmentId);
        using (var output = _storage.Create(name))
        {
            _builder.WriteTo(output);
        }

        var reader = SegmentReader.Open(_storage, name);
        _refs.Register(name, reader);
        AddSegment(new SegmentState(reader, _builder.CopyDeleted(), deletionGeneration: 0) { DeletionsDirty = _builder.DeletedCount > 0 });
        _builder = new SegmentBuilder(_nextSegmentId++);
    }

    private long CommitLocked()
    {
        FlushBuilder();
        if (!_dirty)
        {
            return _generation;
        }

        var referenced = _live.Values.Select(l => GetDoc(l).ContentSegment).ToHashSet();
        var kept = _segments.Where(s => s.LiveCount > 0 || referenced.Contains(s.Id)).ToList();

        var newDeletionGenerations = new Dictionary<long, long>();
        foreach (var segment in kept.Where(s => s.DeletionsDirty))
        {
            var generation = segment.DeletionGeneration + 1;
            DeletionFile.Write(_storage, IndexFileNames.Deletions(segment.Id, generation), segment.Id, segment.Deleted);
            newDeletionGenerations[segment.Id] = generation;
        }

        var manifest = new Manifest(
            Manifest.CurrentFormatVersion,
            _generation + 1,
            _nextSegmentId,
            kept.Select(s => new SegmentInfo(
                s.Id,
                s.Reader.Docs.Count,
                s.Reader.Contents.Count,
                newDeletionGenerations.GetValueOrDefault(s.Id, s.DeletionGeneration),
                s.DeletedCount,
                s.Reader.FileLength)).ToList());
        manifest.Write(_storage);

        // Committed: from here on only in-memory bookkeeping and best-effort cleanup.
        foreach (var (id, generation) in newDeletionGenerations)
        {
            _byId[id].DeletionGeneration = generation;
            _byId[id].DeletionsDirty = false;
        }

        foreach (var dropped in _segments.Except(kept).ToList())
        {
            RemoveSegment(dropped);
        }

        _generation = manifest.Generation;
        RetainCommit(manifest.Files().ToArray());
        PublishSnapshot();
        _dirty = false;
        _refs.RetryPendingDeletes();
        return _generation;
    }

    private void RetainCommit(string[] files)
    {
        _refs.IncRef(files);
        _retainedCommits.Enqueue(files);
        while (_retainedCommits.Count > Math.Max(1, _options.RetainedCommits))
        {
            _refs.DecRef(_retainedCommits.Dequeue());
        }
    }

    private void PublishSnapshot()
    {
        var segments = _segments.Select(s => (s.Reader, (BitArray?)s.Deleted.Clone())).ToList();
        var files = _segments.SelectMany(s => s.Files()).ToList();
        _refs.IncRef(files);
        var snapshot = new IndexSnapshot(_generation, segments, files, released => _refs.DecRef(released.Files));
        var previous = _current;
        _current = snapshot;
        previous?.Dispose();
    }

    private void AddSegment(SegmentState state)
    {
        _segments.Add(state);
        _byId[state.Id] = state;
    }

    private void RemoveSegment(SegmentState state)
    {
        _segments.Remove(state);
        _byId.Remove(state.Id);
        _refs.DecRef([IndexFileNames.Segment(state.Id)]);
    }

    /// <summary>Removes every live document stored in, or whose content lives in, the segment.</summary>
    private int DropSegmentLocked(SegmentState segment)
    {
        var affected = _live
            .Where(kv => kv.Value.SegmentId == segment.Id || GetDoc(kv.Value).ContentSegment == segment.Id)
            .ToList();
        foreach (var (path, location) in affected)
        {
            _live.Remove(path);
            Tombstone(location);
        }

        return affected.Count;
    }

    private void Load()
    {
        var issues = new List<IndexIssue>();
        var files = _storage.List();
        foreach (var temp in files.Where(f => f.EndsWith(IndexFileNames.TempSuffix, StringComparison.Ordinal)))
        {
            _refs.DeleteUnreferenced(temp);
        }

        var manifests = files
            .Select(f => (Name: f, Ok: IndexFileNames.TryParseManifest(f, out var g), Generation: g))
            .Where(m => m.Ok)
            .OrderByDescending(m => m.Generation)
            .ToList();
        var maxSegmentId = files
            .Select(f => IndexFileNames.TryParseSegmentIdOfAny(f, out var id) ? id : 0)
            .DefaultIfEmpty(0)
            .Max();

        Manifest? chosen = null;
        foreach (var (name, _, _) in manifests)
        {
            try
            {
                chosen = Manifest.Read(_storage, name);
                break;
            }
            catch (IndexFormatVersionException ex)
            {
                issues.Add(new IndexIssue(IndexIssueKind.FormatVersionMismatch, name, ex.Message));
            }
            catch (IndexCorruptException ex)
            {
                issues.Add(new IndexIssue(IndexIssueKind.ManifestUnreadable, name, ex.Message));
            }
        }

        if (chosen is null && manifests.Count > 0)
        {
            issues.Add(new IndexIssue(IndexIssueKind.FullRebuildRequired, "", "找不到可用的索引清單，索引將完整重建"));
        }

        _generation = Math.Max(manifests.Count > 0 ? manifests[0].Generation : 0, chosen?.Generation ?? 0);
        _nextSegmentId = Math.Max(chosen?.NextSegmentId ?? 1, maxSegmentId + 1);
        _dirty = chosen is null && manifests.Count > 0;

        if (chosen is not null)
        {
            foreach (var info in chosen.Segments)
            {
                LoadSegment(info, issues);
            }

            RetainCommit(chosen.Files().ToArray());
        }

        RebuildLiveMap(issues);

        var keep = chosen?.Files().ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var file in files.Where(f => IsIndexFile(f) && !keep.Contains(f)))
        {
            _refs.DeleteUnreferenced(file);
        }

        _builder = new SegmentBuilder(_nextSegmentId++);
        PublishSnapshot();
        OpenReport = new IndexOpenReport(issues);

        if (_dirty)
        {
            CommitLocked();
        }
    }

    private void LoadSegment(SegmentInfo info, List<IndexIssue> issues)
    {
        var name = IndexFileNames.Segment(info.Id);
        SegmentReader? reader = null;
        try
        {
            reader = SegmentReader.Open(_storage, name);
            if (reader.SegmentId != info.Id || reader.Docs.Count != info.DocCount || reader.Contents.Count != info.ContentCount)
            {
                throw new IndexCorruptException($"片段檔 {name} 與清單記錄不一致");
            }

            BitArray deleted;
            try
            {
                deleted = info.DeletionGeneration > 0
                    ? DeletionFile.Read(_storage, IndexFileNames.Deletions(info.Id, info.DeletionGeneration), info.Id, info.DocCount)
                    : new BitArray(info.DocCount);
            }
            catch (Exception ex) when (ex is IndexCorruptException or FileNotFoundException)
            {
                reader.Dispose();
                issues.Add(new IndexIssue(IndexIssueKind.DeletionsCorrupt, name, ex.Message, info.DocCount - info.DeletedCount));
                _dirty = true;
                return;
            }

            _refs.Register(name, reader);
            AddSegment(new SegmentState(reader, deleted, info.DeletionGeneration));
        }
        catch (IndexFormatVersionException ex)
        {
            reader?.Dispose();
            issues.Add(new IndexIssue(IndexIssueKind.FormatVersionMismatch, name, ex.Message, info.DocCount - info.DeletedCount));
            _dirty = true;
        }
        catch (IndexCorruptException ex)
        {
            reader?.Dispose();
            issues.Add(new IndexIssue(IndexIssueKind.SegmentCorrupt, name, ex.Message, info.DocCount - info.DeletedCount));
            _dirty = true;
        }
        catch (FileNotFoundException ex)
        {
            issues.Add(new IndexIssue(IndexIssueKind.SegmentMissing, name, $"片段檔 {name} 遺失（{ex.Message}）", info.DocCount - info.DeletedCount));
            _dirty = true;
        }
    }

    private void RebuildLiveMap(List<IndexIssue> issues)
    {
        var dangling = 0;
        foreach (var segment in _segments)
        {
            var docs = segment.Reader.Docs;
            for (var d = 0; d < docs.Count; d++)
            {
                if (segment.Deleted[d])
                {
                    continue;
                }

                var location = new DocLocation(segment.Id, d);
                if (!_byId.ContainsKey(docs[d].ContentSegment))
                {
                    segment.MarkDeleted(d);
                    dangling++;
                    _dirty = true;
                    continue;
                }

                if (_live.TryGetValue(docs[d].Path, out var duplicate))
                {
                    Tombstone(duplicate);
                }

                _live[docs[d].Path] = location;
            }
        }

        if (dangling > 0)
        {
            issues.Add(new IndexIssue(IndexIssueKind.DanglingDocuments, "", $"{dangling} 份文件的內容所在片段已損毀，將重新索引", dangling));
        }
    }

    private static bool IsIndexFile(string name) =>
        name.StartsWith("seg_", StringComparison.Ordinal)
        || name.StartsWith("manifest_", StringComparison.Ordinal)
        || name.EndsWith(IndexFileNames.TempSuffix, StringComparison.Ordinal);

    private readonly record struct DocLocation(long SegmentId, int DocId);

    private sealed class SegmentState(SegmentReader reader, BitArray deleted, long deletionGeneration)
    {
        public SegmentReader Reader { get; } = reader;

        public long Id => Reader.SegmentId;

        public BitArray Deleted { get; } = deleted;

        public int DeletedCount { get; private set; } = CountSet(deleted);

        public long DeletionGeneration { get; set; } = deletionGeneration;

        public bool DeletionsDirty { get; set; }

        public int LiveCount => Reader.Docs.Count - DeletedCount;

        public void MarkDeleted(int docId)
        {
            if (!Deleted[docId])
            {
                Deleted[docId] = true;
                DeletedCount++;
                DeletionsDirty = true;
            }
        }

        public IEnumerable<string> Files()
        {
            yield return IndexFileNames.Segment(Id);
            if (DeletionGeneration > 0)
            {
                yield return IndexFileNames.Deletions(Id, DeletionGeneration);
            }
        }

        private static int CountSet(BitArray bits)
        {
            var count = 0;
            for (var i = 0; i < bits.Length; i++)
            {
                count += bits[i] ? 1 : 0;
            }

            return count;
        }
    }
}
