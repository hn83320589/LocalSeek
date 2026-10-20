using LocalSeek.Core.Storage;

namespace LocalSeek.Core.Index.Segments;

/// <summary>Encodes one term's postings: (id delta, freq) pairs and, separately, position deltas.</summary>
internal sealed class PostingsEncoder
{
    private readonly ByteBuffer _postings = new(1024);
    private readonly ByteBuffer _positions = new(1024);
    private int _lastId = -1;

    public int Count { get; private set; }

    public ReadOnlySpan<byte> Postings => _postings.Span;

    public ReadOnlySpan<byte> Positions => _positions.Span;

    public void Reset()
    {
        _postings.Clear();
        _positions.Clear();
        _lastId = -1;
        Count = 0;
    }

    public void Add(int id, int frequency, ReadOnlySpan<int> positions)
    {
        if (id <= _lastId)
        {
            throw new InvalidOperationException($"Posting ids must increase ({_lastId} then {id}).");
        }

        _postings.WriteVarInt32(id - _lastId - 1);
        _postings.WriteVarInt32(frequency);
        var last = 0;
        foreach (var p in positions)
        {
            _positions.WriteVarInt32(p - last);
            last = p;
        }

        _lastId = id;
        Count++;
    }
}

/// <summary>Decodes what <see cref="PostingsEncoder"/> wrote.</summary>
internal static class PostingsDecoder
{
    public delegate void PostingVisitor(int id, int frequency);

    public delegate void PositionsVisitor(int id, ReadOnlySpan<int> positions);

    public static void Read(ReadOnlySpan<byte> postings, PostingVisitor visit)
    {
        var reader = new ByteReader(postings);
        var id = -1;
        while (!reader.AtEnd)
        {
            id += reader.ReadVarInt32() + 1;
            visit(id, reader.ReadVarInt32());
        }
    }

    public static void ReadWithPositions(ReadOnlySpan<byte> postings, ReadOnlySpan<byte> positions, PositionsVisitor visit)
    {
        var postingReader = new ByteReader(postings);
        var positionReader = new ByteReader(positions);
        var buffer = new int[16];
        var id = -1;
        while (!postingReader.AtEnd)
        {
            id += postingReader.ReadVarInt32() + 1;
            var frequency = postingReader.ReadVarInt32();
            if (buffer.Length < frequency)
            {
                buffer = new int[Math.Max(frequency, buffer.Length * 2)];
            }

            var position = 0;
            for (var i = 0; i < frequency; i++)
            {
                position += positionReader.ReadVarInt32();
                buffer[i] = position;
            }

            visit(id, buffer.AsSpan(0, frequency));
        }
    }
}
