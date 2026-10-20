using System.IO.Hashing;
using LocalSeek.Core.Storage;

namespace LocalSeek.Core.Index.Segments;

/// <summary>
/// Streams a segment file: header, contents and docs are written up front, then terms must be added in
/// ascending key order, then <see cref="Finish"/> writes the term dictionary, term index and footer and
/// flushes. Memory use is bounded by the term dictionary, not by postings size.
/// </summary>
internal sealed class SegmentWriter
{
    private readonly IIndexOutput _output;
    private readonly (long Offset, long Length, uint Crc)[] _sections = new (long, long, uint)[SegmentFormat.SectionCount];
    private readonly Crc32 _crc = new();
    private readonly ByteBuffer _termDict = new(4096);
    private readonly ByteBuffer _termIndex = new(1024);
    private readonly uint _headerCrc;
    private int _section = -1;
    private long _sectionStart;
    private long _postingsLength;
    private byte[] _previousKey = [];
    private int _inBlock;
    private int _blockCount;
    private bool _finished;

    public SegmentWriter(IIndexOutput output, long segmentId, IReadOnlyList<ContentEntry> contents, IReadOnlyList<DocEntry> docs)
    {
        _output = output;

        var header = new ByteBuffer(SegmentFormat.HeaderSize);
        header.WriteBytes(SegmentFormat.HeaderMagic);
        header.WriteUInt16(SegmentFormat.Version);
        header.WriteUInt16(0);
        header.WriteUInt32(0);
        header.WriteInt64(segmentId);
        header.WriteInt64(0);
        _output.Write(header.Span);
        _headerCrc = Crc32.HashToUInt32(header.Span);

        var buffer = new ByteBuffer(64 * 1024);
        buffer.WriteVarInt32(contents.Count);
        foreach (var c in contents)
        {
            buffer.WriteVarInt32(c.BodyLength);
            buffer.WriteUInt64(c.ContentHash);
        }

        WriteWholeSection(SegmentFormat.ContentsSection, buffer.Span);

        buffer.Clear();
        buffer.WriteVarInt32(docs.Count);
        foreach (var d in docs)
        {
            buffer.WriteString(d.Path);
            buffer.WriteVarInt64(d.Size);
            buffer.WriteInt64(d.LastWriteTicks);
            buffer.WriteVarInt32(d.NameLength);
            buffer.WriteVarInt64(d.ContentSegment);
            buffer.WriteVarInt32(d.ContentId);
        }

        WriteWholeSection(SegmentFormat.DocsSection, buffer.Span);
        BeginSection(SegmentFormat.PostingsSection);
    }

    public void AddTerm(ReadOnlySpan<byte> key, int documentFrequency, ReadOnlySpan<byte> postings, ReadOnlySpan<byte> positions)
    {
        if (key.SequenceCompareTo(_previousKey) <= 0)
        {
            throw new InvalidOperationException("Terms must be added in strictly ascending key order.");
        }

        if (_inBlock == SegmentFormat.BlockSize)
        {
            _inBlock = 0;
        }

        var shared = 0;
        if (_inBlock == 0)
        {
            _termIndex.WriteVarInt32(key.Length);
            _termIndex.WriteBytes(key);
            _termIndex.WriteVarInt64(_termDict.Length);
            _termDict.WriteVarInt64(_postingsLength);
            _blockCount++;
        }
        else
        {
            shared = key.CommonPrefixLength(_previousKey);
        }

        _termDict.WriteVarInt32(shared);
        _termDict.WriteVarInt32(key.Length - shared);
        _termDict.WriteBytes(key[shared..]);
        _termDict.WriteVarInt32(documentFrequency);
        _termDict.WriteVarInt32(postings.Length);
        _termDict.WriteVarInt32(positions.Length);

        WriteSectionBytes(postings);
        WriteSectionBytes(positions);
        _postingsLength += postings.Length + positions.Length;
        _previousKey = key.ToArray();
        _inBlock++;
    }

    public void Finish()
    {
        if (_finished)
        {
            throw new InvalidOperationException("Segment already finished.");
        }

        _finished = true;
        EndSection();
        WriteWholeSection(SegmentFormat.TermDictSection, _termDict.Span);

        var index = new ByteBuffer(_termIndex.Length + 8);
        index.WriteVarInt32(_blockCount);
        index.WriteBytes(_termIndex.Span);
        WriteWholeSection(SegmentFormat.TermIndexSection, index.Span);

        var footer = new ByteBuffer(SegmentFormat.FooterSize);
        foreach (var (offset, length, crc) in _sections)
        {
            footer.WriteInt64(offset);
            footer.WriteInt64(length);
            footer.WriteUInt32(crc);
        }

        footer.WriteUInt32(_headerCrc);
        footer.WriteUInt32(Crc32.HashToUInt32(footer.Span));
        footer.WriteBytes(SegmentFormat.FooterMagic);
        _output.Write(footer.Span);
        _output.Flush();
    }

    private void WriteWholeSection(int section, ReadOnlySpan<byte> data)
    {
        BeginSection(section);
        WriteSectionBytes(data);
        EndSection();
    }

    private void BeginSection(int section)
    {
        _section = section;
        _sectionStart = _output.Position;
        _crc.Reset();
    }

    private void WriteSectionBytes(ReadOnlySpan<byte> data)
    {
        _output.Write(data);
        _crc.Append(data);
    }

    private void EndSection() =>
        _sections[_section] = (_sectionStart, _output.Position - _sectionStart, _crc.GetCurrentHashAsUInt32());
}
