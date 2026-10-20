using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text.Json;
using LocalSeek.Core.Storage;

namespace LocalSeek.Core.Index;

public sealed record SegmentInfo(long Id, int DocCount, int ContentCount, long DeletionGeneration, int DeletedCount, long FileLength);

/// <summary>
/// A commit point. Stored as UTF-8 JSON followed by jsonLength u32, crc32 u32 and magic "LSEKMAN\0".
/// Written to a .tmp file, flushed, then atomically renamed into place.
/// </summary>
public sealed record Manifest(int FormatVersion, long Generation, long NextSegmentId, IReadOnlyList<SegmentInfo> Segments)
{
    public const int CurrentFormatVersion = 1;

    private const int TrailerSize = 16;

    private static ReadOnlySpan<byte> Magic => "LSEKMAN\0"u8;

    /// <summary>All files this commit needs, including the manifest itself.</summary>
    public IEnumerable<string> Files()
    {
        yield return IndexFileNames.Manifest(Generation);
        foreach (var s in Segments)
        {
            yield return IndexFileNames.Segment(s.Id);
            if (s.DeletionGeneration > 0)
            {
                yield return IndexFileNames.Deletions(s.Id, s.DeletionGeneration);
            }
        }
    }

    internal void Write(IIndexStorage storage)
    {
        var name = IndexFileNames.Manifest(Generation);
        var temp = name + IndexFileNames.TempSuffix;
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        var buffer = new ByteBuffer(json.Length + TrailerSize);
        buffer.WriteBytes(json);
        buffer.WriteInt32(json.Length);
        buffer.WriteUInt32(Crc32.HashToUInt32(json));
        buffer.WriteBytes(Magic);

        using (var output = storage.Create(temp))
        {
            output.Write(buffer.Span);
            output.Flush();
        }

        storage.AtomicRename(temp, name);
    }

    internal static Manifest Read(IIndexStorage storage, string name)
    {
        byte[] bytes;
        using (var input = storage.OpenRead(name))
        {
            bytes = new byte[input.Length];
            input.ReadAt(0, bytes);
        }

        if (bytes.Length < TrailerSize || !bytes.AsSpan(bytes.Length - 8).SequenceEqual(Magic))
        {
            throw new IndexCorruptException($"清單檔 {name} 不完整");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(bytes.Length - TrailerSize));
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 12));
        if (length != bytes.Length - TrailerSize || Crc32.HashToUInt32(bytes.AsSpan(0, length)) != crc)
        {
            throw new IndexCorruptException($"清單檔 {name} 的校驗碼不符");
        }

        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(bytes.AsSpan(0, length));
        }
        catch (JsonException ex)
        {
            throw new IndexCorruptException($"清單檔 {name} 無法解析", ex);
        }

        if (manifest is null || manifest.Segments is null)
        {
            throw new IndexCorruptException($"清單檔 {name} 無法解析");
        }

        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            throw new IndexFormatVersionException($"清單檔 {name} 的版本 {manifest.FormatVersion} 與目前版本 {CurrentFormatVersion} 不符");
        }

        return manifest;
    }
}
