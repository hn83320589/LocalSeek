using System.Buffers;
using System.IO.Hashing;
using LocalSeek.Core.Storage;

namespace LocalSeek.Core.Index.Segments;

/// <summary>
/// Read-only view of a segment file. Opening validates header, footer, contents, docs and term index
/// checksums; the large term dictionary and postings sections are checked by <see cref="VerifyFully"/>.
/// Thread-safe for concurrent readers.
/// </summary>
internal sealed class SegmentReader : IDisposable
{
    private const int VerifyChunkSize = 1 << 20;

    private readonly IIndexInput _input;
    private readonly (long Offset, long Length, uint Crc)[] _sections;
    private readonly byte[][] _blockKeys;
    private readonly long[] _blockOffsets;

    private SegmentReader(string fileName, long segmentId, IIndexInput input, (long, long, uint)[] sections, ContentEntry[] contents, DocEntry[] docs, byte[][] blockKeys, long[] blockOffsets)
    {
        FileName = fileName;
        SegmentId = segmentId;
        _input = input;
        _sections = sections;
        Contents = contents;
        Docs = docs;
        _blockKeys = blockKeys;
        _blockOffsets = blockOffsets;
    }

    public string FileName { get; }

    public long SegmentId { get; }

    public IReadOnlyList<ContentEntry> Contents { get; }

    public IReadOnlyList<DocEntry> Docs { get; }

    public long FileLength => _input.Length;

    public static SegmentReader Open(IIndexStorage storage, string fileName)
    {
        var input = storage.OpenRead(fileName);
        try
        {
            return Open(fileName, input);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private static SegmentReader Open(string fileName, IIndexInput input)
    {
        if (input.Length < SegmentFormat.HeaderSize + SegmentFormat.FooterSize)
        {
            throw new IndexCorruptException($"片段檔 {fileName} 過短");
        }

        var header = new byte[SegmentFormat.HeaderSize];
        input.ReadAt(0, header);
        var headerReader = new ByteReader(header);
        if (!headerReader.ReadBytes(8).SequenceEqual(SegmentFormat.HeaderMagic))
        {
            throw new IndexCorruptException($"片段檔 {fileName} 的檔頭標記錯誤");
        }

        var version = headerReader.ReadUInt16();
        if (version != SegmentFormat.Version)
        {
            throw new IndexFormatVersionException($"片段檔 {fileName} 的版本 {version} 與目前版本 {SegmentFormat.Version} 不符");
        }

        headerReader.ReadUInt16();
        headerReader.ReadUInt32();
        var segmentId = headerReader.ReadInt64();

        var footer = new byte[SegmentFormat.FooterSize];
        input.ReadAt(input.Length - SegmentFormat.FooterSize, footer);
        if (!footer.AsSpan(SegmentFormat.FooterSize - 8).SequenceEqual(SegmentFormat.FooterMagic))
        {
            throw new IndexCorruptException($"片段檔 {fileName} 的檔尾標記錯誤（可能未寫完）");
        }

        var footerReader = new ByteReader(footer);
        var sections = new (long Offset, long Length, uint Crc)[SegmentFormat.SectionCount];
        for (var i = 0; i < sections.Length; i++)
        {
            sections[i] = (footerReader.ReadInt64(), footerReader.ReadInt64(), footerReader.ReadUInt32());
        }

        var headerCrc = footerReader.ReadUInt32();
        var footerCrc = footerReader.ReadUInt32();
        if (Crc32.HashToUInt32(footer.AsSpan(0, footerReader.Position - 4)) != footerCrc)
        {
            throw new IndexCorruptException($"片段檔 {fileName} 的檔尾校驗碼不符");
        }

        if (Crc32.HashToUInt32(header) != headerCrc)
        {
            throw new IndexCorruptException($"片段檔 {fileName} 的檔頭校驗碼不符");
        }

        foreach (var (offset, length, _) in sections)
        {
            if (offset < SegmentFormat.HeaderSize || length < 0 || offset + length > input.Length - SegmentFormat.FooterSize)
            {
                throw new IndexCorruptException($"片段檔 {fileName} 的區段位置無效");
            }
        }

        var contents = ParseContents(ReadVerified(input, sections[SegmentFormat.ContentsSection], fileName));
        var docs = ParseDocs(ReadVerified(input, sections[SegmentFormat.DocsSection], fileName));
        var (keys, offsets) = ParseTermIndex(ReadVerified(input, sections[SegmentFormat.TermIndexSection], fileName));
        return new SegmentReader(fileName, segmentId, input, sections, contents, docs, keys, offsets);
    }

    public void Dispose() => _input.Dispose();

    /// <summary>Checks the CRC of the term dictionary and postings sections (reads the whole file).</summary>
    public void VerifyFully()
    {
        foreach (var section in new[] { SegmentFormat.PostingsSection, SegmentFormat.TermDictSection })
        {
            var (offset, length, expected) = _sections[section];
            var crc = new Crc32();
            var buffer = ArrayPool<byte>.Shared.Rent(VerifyChunkSize);
            try
            {
                for (long done = 0; done < length;)
                {
                    var chunk = (int)Math.Min(VerifyChunkSize, length - done);
                    _input.ReadAt(offset + done, buffer.AsSpan(0, chunk));
                    crc.Append(buffer.AsSpan(0, chunk));
                    done += chunk;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (crc.GetCurrentHashAsUInt32() != expected)
            {
                throw new IndexCorruptException($"片段檔 {FileName} 的內容校驗碼不符");
            }
        }
    }

    public bool TryGetTerm(ReadOnlySpan<byte> key, out TermEntry entry)
    {
        entry = default;
        var block = BlockFor(key);
        if (block < 0)
        {
            return false;
        }

        foreach (var candidate in DecodeBlock(block))
        {
            var order = candidate.Key.AsSpan().SequenceCompareTo(key);
            if (order == 0)
            {
                entry = candidate;
                return true;
            }

            if (order > 0)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>All terms whose key starts with the prefix, in key order.</summary>
    public IEnumerable<TermEntry> TermsWithPrefix(byte[] prefix)
    {
        for (var block = Math.Max(BlockFor(prefix), 0); block < _blockKeys.Length; block++)
        {
            foreach (var entry in DecodeBlock(block))
            {
                var order = entry.Key.AsSpan().SequenceCompareTo(prefix);
                if (entry.Key.AsSpan().StartsWith(prefix))
                {
                    yield return entry;
                }
                else if (order > 0)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>Every term in key order (used by merging).</summary>
    public IEnumerable<TermEntry> AllTerms()
    {
        for (var block = 0; block < _blockKeys.Length; block++)
        {
            foreach (var entry in DecodeBlock(block))
            {
                yield return entry;
            }
        }
    }

    public byte[] ReadPostings(in TermEntry entry)
    {
        var bytes = new byte[entry.PostingsLength];
        _input.ReadAt(_sections[SegmentFormat.PostingsSection].Offset + entry.PostingsOffset, bytes);
        return bytes;
    }

    /// <summary>Postings followed by positions, as one contiguous read.</summary>
    public byte[] ReadPostingsAndPositions(in TermEntry entry)
    {
        var bytes = new byte[entry.PostingsLength + entry.PositionsLength];
        _input.ReadAt(_sections[SegmentFormat.PostingsSection].Offset + entry.PostingsOffset, bytes);
        return bytes;
    }

    private int BlockFor(ReadOnlySpan<byte> key)
    {
        int lo = 0, hi = _blockKeys.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_blockKeys[mid].AsSpan().SequenceCompareTo(key) <= 0)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    private List<TermEntry> DecodeBlock(int block)
    {
        var (dictOffset, dictLength, _) = _sections[SegmentFormat.TermDictSection];
        var start = _blockOffsets[block];
        var end = block + 1 < _blockOffsets.Length ? _blockOffsets[block + 1] : dictLength;
        if (start < 0 || end > dictLength || end < start)
        {
            throw new IndexCorruptException($"片段檔 {FileName} 的詞典區塊位置無效");
        }

        var bytes = new byte[end - start];
        _input.ReadAt(dictOffset + start, bytes);
        var reader = new ByteReader(bytes);
        var postingsOffset = reader.ReadVarInt64();
        var entries = new List<TermEntry>(SegmentFormat.BlockSize);
        var previous = Array.Empty<byte>();
        while (!reader.AtEnd)
        {
            var shared = reader.ReadVarInt32();
            var suffix = reader.ReadBytes(reader.ReadVarInt32());
            if (shared > previous.Length)
            {
                throw new IndexCorruptException($"片段檔 {FileName} 的詞典資料無效");
            }

            var key = new byte[shared + suffix.Length];
            previous.AsSpan(0, shared).CopyTo(key);
            suffix.CopyTo(key.AsSpan(shared));
            var df = reader.ReadVarInt32();
            var postingsLength = reader.ReadVarInt32();
            var positionsLength = reader.ReadVarInt32();
            entries.Add(new TermEntry(key, df, postingsOffset, postingsLength, positionsLength));
            postingsOffset += postingsLength + positionsLength;
            previous = key;
        }

        return entries;
    }

    private static byte[] ReadVerified(IIndexInput input, (long Offset, long Length, uint Crc) section, string fileName)
    {
        var bytes = new byte[section.Length];
        input.ReadAt(section.Offset, bytes);
        if (Crc32.HashToUInt32(bytes) != section.Crc)
        {
            throw new IndexCorruptException($"片段檔 {fileName} 的校驗碼不符");
        }

        return bytes;
    }

    private static ContentEntry[] ParseContents(byte[] bytes)
    {
        var reader = new ByteReader(bytes);
        var contents = new ContentEntry[reader.ReadVarInt32()];
        for (var i = 0; i < contents.Length; i++)
        {
            contents[i] = new ContentEntry(reader.ReadVarInt32(), reader.ReadUInt64());
        }

        return contents;
    }

    private static DocEntry[] ParseDocs(byte[] bytes)
    {
        var reader = new ByteReader(bytes);
        var docs = new DocEntry[reader.ReadVarInt32()];
        for (var i = 0; i < docs.Length; i++)
        {
            docs[i] = new DocEntry(
                reader.ReadString(),
                reader.ReadVarInt64(),
                reader.ReadInt64(),
                reader.ReadVarInt32(),
                reader.ReadVarInt64(),
                reader.ReadVarInt32());
        }

        return docs;
    }

    private static (byte[][] Keys, long[] Offsets) ParseTermIndex(byte[] bytes)
    {
        var reader = new ByteReader(bytes);
        var count = reader.ReadVarInt32();
        var keys = new byte[count][];
        var offsets = new long[count];
        for (var i = 0; i < count; i++)
        {
            keys[i] = reader.ReadBytes(reader.ReadVarInt32()).ToArray();
            offsets[i] = reader.ReadVarInt64();
        }

        return (keys, offsets);
    }
}
