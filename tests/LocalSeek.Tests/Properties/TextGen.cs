using System.Text;

namespace LocalSeek.Tests.Properties;

/// <summary>Random text built from hand-picked fragments that stress normalization and tokenization.</summary>
public static class TextGen
{
    /// <summary>Fragments whose NFKC form maps one source char to exactly one char (or one surrogate pair to itself).</summary>
    public static readonly string[] NonExpanding =
    [
        "a", "b", "z", "x", "A", "Q", "0", "7", "9",
        "Ａ", "ｚ", "５",
        "中", "文", "資", "料", "台", "臺", "灣", "模", "型", "一",
        "㐀", "鿿",
        "𠮷", "𠀀",
        " ", ",", ".", "-", "，", "。", "　", "!", "\n",
        "カ", "é",
    ];

    /// <summary>Fragments that change length under NFKC, plus combining marks.</summary>
    public static readonly string[] Expanding =
    [
        "㈱", "ﬁ", "ﬂ", "㍻", "Ⅻ", "²", "⑴", "㎏", "豈", "⼈", "é", "́",
    ];

    public static readonly string[] All = [.. NonExpanding, .. Expanding];

    public static string Text(Random random, string[] alphabet, int maxFragments)
    {
        var count = random.Next(0, maxFragments + 1);
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            sb.Append(alphabet[random.Next(alphabet.Length)]);
        }

        return sb.ToString();
    }
}
