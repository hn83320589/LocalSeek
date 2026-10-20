using System.Collections;
using System.IO.Hashing;
using LocalSeek.Core.Storage;

namespace LocalSeek.Core.Index.Segments;

/// <summary>
/// Immutable tombstone bitset for one segment generation:
/// magic "LSEKDEL\0" | version u16 | segmentId i64 | docCount i32 | bitset | crc32 u32.
/// </summary>
internal static class DeletionFile
{
    public const ushort Version = 1;

    private static ReadOnlySpan<byte> Magic => "LSEKDEL\0"u8;

    public static void Write(IIndexStorage storage, string name, long segmentId, BitArray deleted)
    {
        var buffer = new ByteBuffer(32 + (deleted.Length / 8));
        buffer.WriteBytes(Magic);
        buffer.WriteUInt16(Version);
        buffer.WriteInt64(segmentId);
        buffer.WriteInt32(deleted.Length);
        var bits = new byte[(deleted.Length + 7) / 8];
        deleted.CopyTo(bits, 0);
        buffer.WriteBytes(bits);
        buffer.WriteUInt32(Crc32.HashToUInt32(buffer.Span));

        using var output = storage.Create(name);
        output.Write(buffer.Span);
        output.Flush();
    }

    public static BitArray Read(IIndexStorage storage, string name, long segmentId, int docCount)
    {
        byte[] bytes;
        using (var input = storage.OpenRead(name))
        {
            bytes = new byte[input.Length];
            input.ReadAt(0, bytes);
        }

        if (bytes.Length < 26 || Crc32.HashToUInt32(bytes.AsSpan(0, bytes.Length - 4)) != BitConverter.ToUInt32(bytes, bytes.Length - 4))
        {
            throw new IndexCorruptException($"墓碑檔 {name} 的校驗碼不符");
        }

        var reader = new ByteReader(bytes);
        if (!reader.ReadBytes(8).SequenceEqual(Magic))
        {
            throw new IndexCorruptException($"墓碑檔 {name} 的標記錯誤");
        }

        var version = reader.ReadUInt16();
        if (version != Version)
        {
            throw new IndexFormatVersionException($"墓碑檔 {name} 的版本 {version} 不符");
        }

        if (reader.ReadInt64() != segmentId || reader.ReadInt32() != docCount)
        {
            throw new IndexCorruptException($"墓碑檔 {name} 與片段不一致");
        }

        var bits = reader.ReadBytes((docCount + 7) / 8).ToArray();
        return new BitArray(bits) { Length = docCount };
    }
}
