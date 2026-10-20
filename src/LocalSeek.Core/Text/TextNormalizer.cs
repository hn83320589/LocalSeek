using System.Globalization;
using System.Text;

namespace LocalSeek.Core.Text;

/// <summary>
/// NFKC → lowercase (invariant) → 臺 to 台, applied per "normalization unit" (one code point plus
/// any following combining marks) so every output char can be traced back to its source range.
/// </summary>
public static class TextNormalizer
{
    private const char TaiTraditional = '臺';
    private const char Tai = '台';

    // Lazily filled cache for single-char BMP units, which are the overwhelming majority of input.
    // Races are benign: two threads compute the same immutable string.
    private static readonly string?[] BmpCache = new string?[char.MaxValue + 1];

    public static string Normalize(string text)
    {
        var normalized = new NormalizedText();
        Normalize(text, normalized);
        return normalized.ToString();
    }

    public static void Normalize(ReadOnlySpan<char> source, NormalizedText into)
    {
        into.Clear();
        var i = 0;
        while (i < source.Length)
        {
            var start = i;
            i += CodePointLength(source, i);
            while (i < source.Length && IsCombiningMark(source, i))
            {
                i += CodePointLength(source, i);
            }

            var unit = source[start..i];
            into.Append(unit.Length == 1 ? NormalizeBmp(unit[0]) : NormalizeUnit(unit), start, i);
        }
    }

    internal static int CodePointLength(ReadOnlySpan<char> s, int index) =>
        char.IsHighSurrogate(s[index]) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1]) ? 2 : 1;

    private static bool IsCombiningMark(ReadOnlySpan<char> s, int index)
    {
        var c = s[index];
        if (c < '̀')
        {
            return false;
        }

        var category = CodePointLength(s, index) == 2
            ? CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, s[index + 1]))
            : CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    private static string NormalizeBmp(char c)
    {
        var cached = BmpCache[c];
        if (cached is null)
        {
            cached = NormalizeUnit([c]);
            BmpCache[c] = cached;
        }

        return cached;
    }

    private static string NormalizeUnit(ReadOnlySpan<char> unit)
    {
        var text = new string(unit);
        if (char.IsSurrogate(unit[0]))
        {
            // A lone surrogate is ill-formed UTF-16 that string.Normalize rejects; it is a separator anyway.
            return text;
        }

        try
        {
            return text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace(TaiTraditional, Tai);
        }
        catch (ArgumentException)
        {
            // Noncharacters such as U+FFFE are rejected by the normalizer. They are separators either
            // way, so the unit passes through unchanged; arbitrary file content must never abort indexing.
            return text;
        }
    }
}
