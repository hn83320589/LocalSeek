using System.Globalization;
using System.Text;

namespace LocalSeek.Tests.Reference;

/// <summary>
/// Independent tokenizer used only by the reference engine. Deliberately written differently from
/// LocalSeek.Core: it normalizes the whole string at once, materializes code points, and tracks no
/// source coordinates. It must not call into LocalSeek.Core tokenization code.
/// </summary>
public static class ReferenceTokenizer
{
    public sealed record RefToken(string Text, string Kind, int Position);

    public sealed record RefTokenized(List<RefToken> Tokens, Dictionary<string, int> CjkCharCounts)
    {
        public int Length => Tokens.Count;
    }

    public static string Normalize(string text)
    {
        // Drop lone surrogates first: they cannot be normalized and are separators anyway.
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(text, i, 2);
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(text[i]);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace("臺", "台");
    }

    public static bool IsCjk(int cp) =>
        (cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x20000 && cp <= 0x2EBEF);

    public static bool IsLatin(int cp) => (cp >= 'a' && cp <= 'z') || (cp >= '0' && cp <= '9');

    public static RefTokenized Tokenize(string text)
    {
        var codePoints = new List<int>();
        var normalized = Normalize(text);
        for (var i = 0; i < normalized.Length; i += char.IsSurrogatePair(normalized, i) ? 2 : 1)
        {
            codePoints.Add(char.ConvertToUtf32(normalized, i));
        }

        var tokens = new List<RefToken>();
        var chars = new Dictionary<string, int>(StringComparer.Ordinal);
        var position = 0;
        var k = 0;
        while (k < codePoints.Count)
        {
            if (IsLatin(codePoints[k]))
            {
                var sb = new StringBuilder();
                while (k < codePoints.Count && IsLatin(codePoints[k]))
                {
                    sb.Append((char)codePoints[k]);
                    k++;
                }

                tokens.Add(new RefToken(sb.ToString(), "Latin", position));
                position++;
            }
            else if (IsCjk(codePoints[k]))
            {
                var run = new List<string>();
                while (k < codePoints.Count && IsCjk(codePoints[k]))
                {
                    run.Add(char.ConvertFromUtf32(codePoints[k]));
                    k++;
                }

                foreach (var c in run)
                {
                    chars[c] = chars.GetValueOrDefault(c) + 1;
                }

                if (run.Count == 1)
                {
                    tokens.Add(new RefToken(run[0], "CjkSingle", position));
                    position++;
                }
                else
                {
                    for (var j = 0; j + 1 < run.Count; j++)
                    {
                        tokens.Add(new RefToken(run[j] + run[j + 1], "CjkBigram", position + j));
                    }

                    position += run.Count - 1;
                }
            }
            else
            {
                k++;
            }
        }

        return new RefTokenized(tokens, chars);
    }

    /// <summary>Unicode category check kept here so tests can build "minimal range" assertions without Core.</summary>
    public static bool IsCombiningMark(string s, int index)
    {
        var cat = CharUnicodeInfo.GetUnicodeCategory(s, index);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }
}
