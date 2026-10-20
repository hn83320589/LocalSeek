namespace LocalSeek.Core.Text;

/// <summary>
/// Reusable buffer holding normalized text plus, for every normalized UTF-16 char, the
/// [start, end) range of the source unit it came from. Not thread-safe.
/// </summary>
public sealed class NormalizedText
{
    private char[] _chars = new char[256];
    private int[] _sourceStart = new int[256];
    private int[] _sourceEnd = new int[256];

    public int Length { get; private set; }

    public ReadOnlySpan<char> Chars => _chars.AsSpan(0, Length);

    public int SourceStart(int index) => _sourceStart[index];

    public int SourceEnd(int index) => _sourceEnd[index];

    public override string ToString() => new(Chars);

    internal void Clear() => Length = 0;

    internal void Append(ReadOnlySpan<char> text, int sourceStart, int sourceEnd)
    {
        var required = Length + text.Length;
        if (required > _chars.Length)
        {
            var capacity = Math.Max(required, _chars.Length * 2);
            Array.Resize(ref _chars, capacity);
            Array.Resize(ref _sourceStart, capacity);
            Array.Resize(ref _sourceEnd, capacity);
        }

        text.CopyTo(_chars.AsSpan(Length));
        _sourceStart.AsSpan(Length, text.Length).Fill(sourceStart);
        _sourceEnd.AsSpan(Length, text.Length).Fill(sourceEnd);
        Length = required;
    }
}
