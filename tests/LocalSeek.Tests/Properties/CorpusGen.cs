using System.Text;
using LocalSeek.Core.Index;
using LocalSeek.Tests.Reference;

namespace LocalSeek.Tests.Properties;

/// <summary>Random mixed CJK/Latin corpora and queries over a small vocabulary so that hits are frequent.</summary>
public static class CorpusGen
{
    public static readonly string[] LatinWords =
        ["apple", "apply", "applied", "banana", "band", "data", "database", "ai", "model", "report", "x1", "2024", "alpha", "beta"];

    public static readonly string[] CjkChars =
        ["台", "灣", "大", "學", "資", "料", "模", "型", "分", "析", "報", "告", "臺", "\uD842\uDFB7"];

    private static readonly string[] Separators = [" ", " ", " ", "，", "。", "-", "\n", "、", ""];
    private static readonly string[] Extensions = ["txt", "md", "html", "docx", "csv", "TXT"];
    private static readonly string[] Directories = ["A", "b", "資料", "Reports", "x/y"];

    public static List<ReferenceDocument> Documents(Random random, int count)
    {
        var docs = new List<ReferenceDocument>(count);
        for (var i = 0; i < count; i++)
        {
            var name = new StringBuilder();
            for (var k = random.Next(0, 3); k > 0; k--)
            {
                name.Append(Fragment(random)).Append(random.Next(2) == 0 ? "_" : " ");
            }

            name.Append(i).Append('.').Append(Extensions[random.Next(Extensions.Length)]);
            var dir = Directories[random.Next(Directories.Length)].Replace('/', '\\');
            var path = $@"C:\corpus\{dir}\{name}";

            var body = new StringBuilder();
            for (var k = random.Next(0, 60); k > 0; k--)
            {
                body.Append(Fragment(random)).Append(Separators[random.Next(Separators.Length)]);
            }

            var modified = new DateTime(2023, 12, 25, 0, 0, 0, DateTimeKind.Utc).AddHours(random.Next(0, 24 * 16));
            docs.Add(new ReferenceDocument(path, random.Next(0, 3 * 1024 * 1024), modified, body.ToString()));
        }

        return docs;
    }

    public static IEnumerable<DocumentInput> ToInputs(IEnumerable<ReferenceDocument> docs) =>
        docs.Select(d => new DocumentInput(d.Path, d.Size, d.LastWriteUtc, d.Body));

    public static string Fragment(Random random) => random.Next(10) switch
    {
        < 5 => LatinWord(random),
        < 9 => CjkRun(random, random.Next(1, 6)),
        _ => LatinWord(random) + CjkRun(random, random.Next(1, 4)),
    };

    public static string LatinWord(Random random)
    {
        var word = LatinWords[random.Next(LatinWords.Length)];
        return random.Next(8) switch
        {
            0 => word.ToUpperInvariant(),
            1 => string.Concat(word.Select(c => c is >= 'a' and <= 'z' ? (char)(c - 'a' + 'Ａ') : c)),
            _ => word,
        };
    }

    public static string CjkRun(Random random, int length)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < length; i++)
        {
            sb.Append(CjkChars[random.Next(CjkChars.Length)]);
        }

        return sb.ToString();
    }

    /// <summary>A random query string; may be invalid (e.g. only NOT), which both engines must then reject.</summary>
    public static string Query(Random random, int depth = 3)
    {
        if (depth == 0 || random.NextDouble() < 0.35)
        {
            return Leaf(random);
        }

        return random.Next(10) switch
        {
            < 3 => $"{Query(random, depth - 1)} {Query(random, depth - 1)}",
            < 4 => $"{Query(random, depth - 1)} AND {Query(random, depth - 1)}",
            < 6 => $"{Query(random, depth - 1)} OR {Query(random, depth - 1)}",
            < 7 => $"{Query(random, depth - 1)} -{Leaf(random)}",
            < 8 => $"{Query(random, depth - 1)} NOT ({Query(random, depth - 1)})",
            _ => $"({Query(random, depth - 1)})",
        };
    }

    public static string Leaf(Random random) => random.Next(20) switch
    {
        < 8 => LatinWord(random),
        < 12 => CjkRun(random, random.Next(1, 5)),
        < 14 => $"\"{PhraseText(random)}\"",
        < 16 => LatinWords[random.Next(LatinWords.Length)][..2] + "*",
        < 17 => LatinWord(random) + CjkRun(random, 2),
        < 18 => "ext:" + Extensions[random.Next(Extensions.Length)],
        < 19 => random.Next(2) == 0 ? "size:>1MB" : "path:資料",
        _ => "modified:>=2024-01-01",
    };

    private static string PhraseText(Random random)
    {
        var parts = new List<string>();
        for (var i = random.Next(2, 4); i > 0; i--)
        {
            parts.Add(random.Next(2) == 0 ? LatinWord(random) : CjkRun(random, random.Next(2, 4)));
        }

        return string.Join(random.Next(2) == 0 ? " " : "", parts);
    }
}
