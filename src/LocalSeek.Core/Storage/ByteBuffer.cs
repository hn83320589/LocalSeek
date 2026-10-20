using System.Buffers.Binary;
using System.Text;
using LocalSeek.Core.Index;

namespace LocalSeek.Core.Storage;

/// <summary>Growable little-endian byte writer with LEB128 varints.</summary>
internal sealed class ByteBuffer(int capacity = 256)
{
    private byte[] _data = new byte[capacity];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Span => _data.AsSpan(0, Length);

    public void Clear() => Length = 0;

    public void WriteByte(byte value)
    {
        Ensure(1);
        _data[Length++] = value;
    }

    public void WriteVarUInt64(ulong value)
    {
        Ensure(10);
        while (value >= 0x80)
        {
            _data[Length++] = (byte)(value | 0x80);
            value >>= 7;
        }

        _data[Length++] = (byte)value;
    }

    public void WriteVarInt32(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        WriteVarUInt64((uint)value);
    }

    public void WriteVarInt64(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        WriteVarUInt64((ulong)value);
    }

    public void WriteUInt16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(Length), value);
        Length += 2;
    }

    public void WriteUInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(Length), value);
        Length += 4;
    }

    public void WriteInt32(int value) => WriteUInt32((uint)value);

    public void WriteInt64(long value) => WriteUInt64((ulong)value);

    public void WriteUInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(Length), value);
        Length += 8;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_data.AsSpan(Length));
        Length += bytes.Length;
    }

    public void WriteString(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        WriteVarInt32(count);
        Ensure(count);
        Encoding.UTF8.GetBytes(value, _data.AsSpan(Length));
        Length += count;
    }

    private void Ensure(int extra)
    {
        if (Length + extra > _data.Length)
        {
            Array.Resize(ref _data, Math.Max(Length + extra, _data.Length * 2));
        }
    }
}

/// <summary>Reader counterpart of <see cref="ByteBuffer"/>. Malformed input raises <see cref="IndexCorruptException"/>.</summary>
internal ref struct ByteReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; private set; }

    public readonly bool AtEnd => Position >= _data.Length;

    public byte ReadByte()
    {
        if (Position >= _data.Length)
        {
            throw new IndexCorruptException("資料提前結束");
        }

        return _data[Position++];
    }

    public ulong ReadVarUInt64()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return result;
            }
        }

        throw new IndexCorruptException("varint 過長");
    }

    public int ReadVarInt32()
    {
        var value = ReadVarUInt64();
        return value <= int.MaxValue ? (int)value : throw new IndexCorruptException("數值超出範圍");
    }

    public long ReadVarInt64()
    {
        var value = ReadVarUInt64();
        return value <= long.MaxValue ? (long)value : throw new IndexCorruptException("數值超出範圍");
    }

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));

    public int ReadInt32() => (int)ReadUInt32();

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));

    public long ReadInt64() => (long)ReadUInt64();

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || Position + count > _data.Length)
        {
            throw new IndexCorruptException("資料提前結束");
        }

        var slice = _data.Slice(Position, count);
        Position += count;
        return slice;
    }

    public string ReadString()
    {
        var bytes = ReadBytes(ReadVarInt32());
        return Encoding.UTF8.GetString(bytes);
    }
}
