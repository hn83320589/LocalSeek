using System.Text;

namespace LocalSeek.Core.Index.Segments;

/// <summary>Body length and raw-content hash of one indexed content; body postings are keyed by its id.</summary>
public readonly record struct ContentEntry(int BodyLength, ulong ContentHash);

/// <summary>
/// A document: path, metadata and file-name field, plus a reference to the content holding its body
/// postings. The content may live in another segment (a rename/move reuses it without re-tokenizing).
/// </summary>
public readonly record struct DocEntry(string Path, long Size, long LastWriteTicks, int NameLength, long ContentSegment, int ContentId)
{
    public DateTime LastWriteUtc => new(LastWriteTicks, DateTimeKind.Utc);
}

/// <summary>Location of a term's postings inside a segment.</summary>
public readonly record struct TermEntry(byte[] Key, int DocFrequency, long PostingsOffset, int PostingsLength, int PositionsLength);

/// <summary>
/// Segment file layout (little-endian; varints are LEB128):
/// <code>
/// Header(32)  magic "LSEKSEG\0" | version u16 | reserved u16+u32 | segmentId i64 | reserved i64
/// Contents    count, [bodyLength varint, contentHash u64]
/// Docs        count, [path str, size varlong, lastWriteTicks i64, nameLength varint, contentSegment varlong, contentId varint]
/// Postings    per term: [doc delta varint, freq varint]* then [position delta varint]* per posting
/// TermDict    blocks of ≤64 terms: startPostingsOffset varlong, then entries
///             [shared varint, suffixLen varint, suffix, docFreq varint, postingsLen varint, positionsLen varint]
/// TermIndex   count, [keyLen varint, firstKey, blockOffset varlong]
/// Footer      5 × [offset i64, length i64, crc32 u32] | headerCrc u32 | footerCrc u32 | magic "LSEKEND\0"
/// </code>
/// Term keys are [TermSpace byte] + UTF-8 term, compared bytewise.
/// </summary>
internal static class SegmentFormat
{
    public const ushort Version = 1;
    public const int HeaderSize = 32;
    public const int SectionCount = 5;
    public const int FooterSize = (SectionCount * 20) + 4 + 4 + 8;
    public const int BlockSize = 64;

    public const int ContentsSection = 0;
    public const int DocsSection = 1;
    public const int PostingsSection = 2;
    public const int TermDictSection = 3;
    public const int TermIndexSection = 4;

    public static ReadOnlySpan<byte> HeaderMagic => "LSEKSEG\0"u8;

    public static ReadOnlySpan<byte> FooterMagic => "LSEKEND\0"u8;

    public static byte[] Key(TermSpace space, string term)
    {
        var key = new byte[1 + Encoding.UTF8.GetByteCount(term)];
        key[0] = (byte)space;
        Encoding.UTF8.GetBytes(term, key.AsSpan(1));
        return key;
    }

    public static string TermOf(ReadOnlySpan<byte> key) => Encoding.UTF8.GetString(key[1..]);
}
