// Generates a deterministic test corpus of mixed Chinese/Latin documents.
//
//   dotnet run -c Release --project tools/LocalSeek.CorpusGen -- --out D:\perf\corpus [--count 50000]
//        [--avg-kb 20] [--seed 1] [--folders 500] [--mixed]
//
// --mixed produces the file-type and encoding mix LocalSeek must handle (UTF-8 with and without BOM,
// UTF-16 LE, Big5, HTML, DOCX, CSV, Markdown); without it every file is UTF-8 .txt.
// Word frequencies follow a Zipf distribution so queries behave like they do on real text.
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("usage: LocalSeek.CorpusGen --out <dir> [--count 50000] [--avg-kb 20] [--seed 1] [--folders 500] [--mixed]");
    return 2;
}

var stopwatch = Stopwatch.StartNew();
var vocabulary = new Vocabulary(options.Seed);
Directory.CreateDirectory(options.Output);
long totalBytes = 0;
var done = 0;
Parallel.For(0, options.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
{
    var random = new Random(HashCode.Combine(options.Seed, i));
    var folder = Path.Combine(options.Output, $"d{i % options.Folders:D3}");
    Directory.CreateDirectory(folder);
    var kind = options.Mixed ? DocumentKind.Pick(random) : DocumentKind.Utf8Text;
    var targetBytes = (int)(options.AverageKilobytes * 1024 * (0.5 + random.NextDouble()));
    var text = vocabulary.Text(random, targetBytes);
    var name = $"{i:D6}_{vocabulary.FileWord(random)}{kind.Extension}";
    var bytes = kind.Encode(text, vocabulary, random);
    File.WriteAllBytes(Path.Combine(folder, name), bytes);
    Interlocked.Add(ref totalBytes, bytes.Length);
    var count = Interlocked.Increment(ref done);
    if (count % 5000 == 0)
    {
        Console.WriteLine($"{count:N0} / {options.Count:N0}");
    }
});

Console.WriteLine($"Wrote {options.Count:N0} files, {totalBytes / (1024.0 * 1024):F1} MB, average {totalBytes / 1024.0 / options.Count:F1} KB, in {stopwatch.Elapsed.TotalSeconds:F1} s → {options.Output}");
return 0;

internal sealed record Options(string Output, int Count, double AverageKilobytes, int Seed, int Folders, bool Mixed)
{
    public static Options? Parse(string[] args)
    {
        string? output = null;
        int count = 50_000, seed = 1, folders = 500;
        double averageKb = 20;
        var mixed = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": output = args[++i]; break;
                case "--count": count = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--avg-kb": averageKb = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--folders": folders = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--mixed": mixed = true; break;
                default: return null;
            }
        }

        return output is null ? null : new Options(output, count, averageKb, seed, Math.Max(1, folders), mixed);
    }
}

/// <summary>
/// Zipf-distributed CJK and Latin words. CJK characters are Big5 level-1 (common) ideographs, like real
/// Traditional Chinese text; LocalSeek rejects Big5 files dominated by rare characters as likely GBK.
/// </summary>
internal sealed class Vocabulary
{
    private const string Punctuation = "，。、；：！？";

    private readonly string[] _cjkWords;
    private readonly string[] _latinWords;
    private readonly double[] _cjkWeights;
    private readonly double[] _latinWeights;

    public Vocabulary(int seed)
    {
        var random = new Random(seed);
        var big5 = Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var pool = new List<char>();
        for (var c = (char)0x4E00; c <= 0x9FA5; c++)
        {
            try
            {
                var bytes = big5.GetBytes(c.ToString());
                if (((bytes[0] << 8) | bytes[1]) is >= 0xA440 and <= 0xC67E)
                {
                    pool.Add(c);
                }
            }
            catch (EncoderFallbackException)
            {
                // Not in Big5: never used, so every generated document can also be written as Big5.
            }
        }

        var chars = pool.OrderBy(_ => random.Next()).Take(3500).ToArray();
        var charWeights = Cumulative(chars.Length, 1.0);
        _cjkWords = Enumerable.Range(0, 20_000).Select(_ =>
        {
            var length = random.NextDouble() switch { < 0.1 => 1, < 0.7 => 2, < 0.9 => 3, _ => 4 };
            var word = new StringBuilder();
            for (var k = 0; k < length; k++)
            {
                word.Append(chars[Sample(charWeights, random)]);
            }

            return word.ToString();
        }).ToArray();
        _latinWords = Enumerable.Range(0, 6_000).Select(i =>
        {
            if (i % 50 == 0)
            {
                return random.Next(1, 100_000).ToString(CultureInfo.InvariantCulture);
            }

            var word = new StringBuilder();
            for (var k = random.Next(2, 10); k > 0; k--)
            {
                word.Append((char)('a' + random.Next(26)));
            }

            return word.ToString();
        }).ToArray();
        _cjkWeights = Cumulative(_cjkWords.Length, 1.0);
        _latinWeights = Cumulative(_latinWords.Length, 1.0);
    }

    public string FileWord(Random random) =>
        random.Next(2) == 0 ? _cjkWords[Sample(_cjkWeights, random)] : _latinWords[Sample(_latinWeights, random)];

    /// <summary>Chinese sentences with embedded English words, roughly <paramref name="targetBytes"/> in UTF-8.</summary>
    public string Text(Random random, int targetBytes)
    {
        var text = new StringBuilder();
        var bytes = 0;
        while (bytes < targetBytes)
        {
            var words = random.Next(6, 19);
            for (var w = 0; w < words; w++)
            {
                if (random.NextDouble() < 0.75)
                {
                    var word = _cjkWords[Sample(_cjkWeights, random)];
                    text.Append(word);
                    bytes += word.Length * 3;
                }
                else
                {
                    var word = _latinWords[Sample(_latinWeights, random)];
                    text.Append(' ').Append(random.Next(10) == 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word).Append(' ');
                    bytes += word.Length + 2;
                }

                if (random.Next(8) == 0)
                {
                    text.Append(Punctuation[random.Next(Punctuation.Length)]);
                    bytes += 3;
                }
            }

            text.Append('。');
            bytes += 3;
            if (random.Next(5) == 0)
            {
                text.Append('\n');
                bytes++;
            }
        }

        return text.ToString();
    }

    private static double[] Cumulative(int count, double exponent)
    {
        var cumulative = new double[count];
        var sum = 0.0;
        for (var r = 0; r < count; r++)
        {
            sum += 1.0 / Math.Pow(r + 1, exponent);
            cumulative[r] = sum;
        }

        for (var r = 0; r < count; r++)
        {
            cumulative[r] /= sum;
        }

        return cumulative;
    }

    private static int Sample(double[] cumulative, Random random)
    {
        var index = Array.BinarySearch(cumulative, random.NextDouble());
        return Math.Min(index < 0 ? ~index : index, cumulative.Length - 1);
    }
}

internal sealed record DocumentKind(string Extension, Func<string, Vocabulary, Random, byte[]> Encode)
{
    private static readonly Encoding Big5 = Encoding.GetEncoding(950);

    public static readonly DocumentKind Utf8Text = new(".txt", (t, _, _) => Encoding.UTF8.GetBytes(t));

    private static readonly (double Share, DocumentKind Kind)[] Mix =
    [
        (0.70, Utf8Text),
        (0.03, new(".md", (t, _, _) => Encoding.UTF8.GetBytes("# 筆記\n\n" + t))),
        (0.05, new(".txt", (t, _, _) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(t)])),
        (0.04, new(".log", (t, _, _) => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(t)])),
        (0.08, new(".txt", (t, _, _) => Big5.GetBytes(t))),
        (0.05, new(".html", (t, _, _) => Encoding.UTF8.GetBytes(Html(t)))),
        (0.03, new(".docx", (t, _, _) => Docx(t))),
        (0.02, new(".csv", (t, _, _) => Encoding.UTF8.GetBytes(Csv(t)))),
    ];

    public static DocumentKind Pick(Random random)
    {
        var roll = random.NextDouble();
        foreach (var (share, kind) in Mix)
        {
            if ((roll -= share) < 0)
            {
                return kind;
            }
        }

        return Utf8Text;
    }

    private static string Html(string text)
    {
        var html = new StringBuilder("<!DOCTYPE html><html><head><title>文件</title><style>p{margin:0}</style></head><body>\n");
        foreach (var line in text.Split('\n'))
        {
            html.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(line)).Append("</p>\n");
        }

        return html.Append("<script>var tracking = 'not indexed';</script></body></html>").ToString();
    }

    private static string Csv(string text) =>
        string.Join("\r\n", text.Split('。', StringSplitOptions.RemoveEmptyEntries).Select(s => string.Join(',', s.Split(' ', StringSplitOptions.RemoveEmptyEntries))));

    private static byte[] Docx(string text)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false));
            writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
            foreach (var line in text.Split('\n'))
            {
                writer.Write("<w:p><w:r><w:t xml:space=\"preserve\">");
                writer.Write(System.Security.SecurityElement.Escape(line));
                writer.Write("</w:t></w:r></w:p>");
            }

            writer.Write("</w:body></w:document>");
        }

        return stream.ToArray();
    }
}
