// Measures the performance limits of the assignment on a generated corpus:
//   build time, two-term AND / phrase P95 latency, index open time, peak memory, save-to-searchable latency.
//
//   dotnet run -c Release --project tests/LocalSeek.Perf -- --corpus D:\perf\corpus --work D:\perf\work
//        [--threads 8] [--queries 300] [--cores 16]
//
// --cores pins the process to the first N logical processors (16 = the eight hyper-threaded P-cores of
// an i7-13700) so results correspond to an 8-core machine.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LocalSeek.Core.Index;
using LocalSeek.Core.Query;
using LocalSeek.Core.Search;
using LocalSeek.Core.Storage;
using LocalSeek.Core.Text;
using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.Extraction;
using LocalSeek.Indexing.FileSystem;
using LocalSeek.Indexing.Pipeline;
using LocalSeek.Indexing.Watching;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

var options = PerfOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("usage: LocalSeek.Perf --corpus <dir> --work <dir> [--threads 8] [--queries 300] [--cores 16]");
    return 2;
}

var process = Process.GetCurrentProcess();
if (options.Cores > 0 && options.Cores < Environment.ProcessorCount)
{
    process.ProcessorAffinity = (nint)((1L << options.Cores) - 1);
}

var report = new StringBuilder();
void Line(string text)
{
    Console.WriteLine(text);
    report.AppendLine(text);
}

var indexDirectory = Path.Combine(options.Work, "index");
var fileSystem = new PhysicalSourceFileSystem();
var extractor = new ContentExtractor(new ExtractionOptions());
var corpusFiles = Directory.EnumerateFiles(options.Corpus, "*", SearchOption.AllDirectories).ToList();
var corpusBytes = corpusFiles.Sum(f => new FileInfo(f).Length);

Line("# LocalSeek 效能量測");
Line("");
Line($"- 日期：{DateTime.Now:yyyy-MM-dd HH:mm}");
Line($"- CPU：{CpuName()}；使用 {(options.Cores > 0 ? options.Cores : Environment.ProcessorCount)} 個邏輯處理器，索引平行度 {options.Threads}");
Line($"- 記憶體：{TotalMemoryGb():F1} GB；作業系統：{Environment.OSVersion.VersionString}；.NET {Environment.Version}");
Line($"- 語料：{corpusFiles.Count:N0} 個檔案，{corpusBytes / 1024.0 / 1024:F1} MB（平均 {corpusBytes / 1024.0 / corpusFiles.Count:F1} KB）");
Line("");

// ------------------------------------------------------------------ 1. full build
if (options.SkipBuild)
{
    Line("（略過建立索引，沿用既有索引）");
    Line("");
    goto Open;
}

if (Directory.Exists(indexDirectory))
{
    Directory.Delete(indexDirectory, recursive: true);
}

var peak = new PeakSampler(process);
var buildTimer = Stopwatch.StartNew();
TimeSpan indexingTime;
IndexStatistics afterBuild;
using (var writer = IndexWriter.Open(new FileSystemIndexStorage(indexDirectory)))
{
    var diagnostics = new DiagnosticsLog(TimeProvider.System);
    var engine = new IndexingEngine(writer, fileSystem, extractor, diagnostics, TimeProvider.System, new IndexingOptions
    {
        Roots = [options.Corpus.TrimEnd('\\')],
        MaxParallelism = options.Threads,
    });
    engine.RequestFullScan();
    await engine.ProcessAsync(includeNotYetDue: true);
    indexingTime = buildTimer.Elapsed;
    while (writer.MaybeMerge())
    {
    }

    afterBuild = writer.GetStatistics();
    Line($"## 1. 完整建立索引");
    Line("");
    Line($"| 項目 | 結果 | 限制 |");
    Line($"|---|---|---|");
    Line($"| 抽取＋分詞＋寫入＋提交 | {indexingTime.TotalSeconds:F1} s | |");
    Line($"| 含背景合併完成 | {buildTimer.Elapsed.TotalSeconds:F1} s | ≤ 240 s |");
    Line($"| 索引文件數 / 片段數 | {afterBuild.LiveDocuments:N0} / {afterBuild.SegmentCount} | |");
    Line($"| 跳過或失敗 | {diagnostics.Count:N0} | |");
    Line($"| 索引大小 | {afterBuild.IndexBytes / 1024.0 / 1024:F1} MB | |");
}

peak.Stop();
Line($"| 記憶體峰值（Working Set） | {peak.PeakMegabytes:F0} MB | ≤ 600 MB |");
Line("");

// ------------------------------------------------------------------ 2. open
Open:
GC.Collect();
GC.WaitForPendingFinalizers();
var openTimer = Stopwatch.StartNew();
using var reopened = IndexWriter.Open(new FileSystemIndexStorage(indexDirectory));
using (var first = reopened.AcquireSnapshot())
{
    _ = first.DocCount;
}

var openTime = openTimer.Elapsed;
Line("## 2. 開啟既有索引");
Line("");
Line($"| 項目 | 結果 | 限制 |");
Line($"|---|---|---|");
Line($"| IndexWriter.Open + 第一個快照 | {openTime.TotalMilliseconds:F0} ms | ≤ 2000 ms |");
Line("");

// ------------------------------------------------------------------ 3. queries
var (andQueries, phraseQueries) = SampleQueries(reopened, extractor, options.Queries);
var andLatencies = Measure(reopened, andQueries);
var phraseLatencies = Measure(reopened, phraseQueries);
Line("## 3. 查詢延遲（Top 200，含解析）");
Line("");
Line($"| 查詢類型 | 筆數 | P50 | P95 | 最大 | 限制 |");
Line($"|---|---|---|---|---|---|");
Line(Row("兩詞 AND", andLatencies));
Line(Row("片語", phraseLatencies));
Line("");
Line($"範例：`{andQueries[0]}`、`{phraseQueries[0]}`");
Line("");

// ------------------------------------------------------------------ 4. save → searchable
var visibility = await MeasureVisibilityAsync(reopened, fileSystem, extractor, options.Corpus.TrimEnd('\\'));
Line("## 4. 存檔後可被搜尋到的時間（含 500 ms 防抖）");
Line("");
Line($"| 次數 | 平均 | 最大 | 限制 |");
Line($"|---|---|---|---|");
Line($"| {visibility.Count} | {visibility.Average():F0} ms | {visibility.Max():F0} ms | ≤ 3000 ms |");

Directory.CreateDirectory(options.Work);
var resultFile = Path.Combine(options.Work, "perf-results.md");
File.WriteAllText(resultFile, report.ToString());
Console.WriteLine($"\n→ {resultFile}");
return 0;

static string Row(string name, List<double> latencies)
{
    latencies.Sort();
    double P(double q) => latencies[Math.Min(latencies.Count - 1, (int)Math.Ceiling(q * latencies.Count) - 1)];
    return $"| {name} | {latencies.Count} | {P(0.5):F1} ms | {P(0.95):F1} ms | {latencies[^1]:F1} ms | P95 ≤ 100 ms |";
}

static List<double> Measure(IndexWriter writer, List<string> queries)
{
    var latencies = new List<double>();
    using var snapshot = writer.AcquireSnapshot();
    foreach (var query in queries.Take(5))
    {
        QueryExecutor.Execute(snapshot, QueryBinder.Bind(QueryParser.Parse(query)), SearchOptions.Default);
    }

    foreach (var query in queries)
    {
        var timer = Stopwatch.StartNew();
        QueryExecutor.Execute(snapshot, QueryBinder.Bind(QueryParser.Parse(query)), SearchOptions.Default);
        latencies.Add(timer.Elapsed.TotalMilliseconds);
    }

    return latencies;
}

/// <summary>Queries built from real document text: two random terms (AND) and 2–3 consecutive terms (phrase).</summary>
static (List<string> And, List<string> Phrase) SampleQueries(IndexWriter writer, ContentExtractor extractor, int count)
{
    var random = new Random(77);
    var andQueries = new List<string>();
    var phraseQueries = new List<string>();
    using var snapshot = writer.AcquireSnapshot();
    while (andQueries.Count < count || phraseQueries.Count < count)
    {
        var path = snapshot.GetMetadata(random.Next(snapshot.DocCount)).Path;
        if (extractor.Extract(path, File.ReadAllBytes(path)) is not ExtractionResult.Success text)
        {
            continue;
        }

        var tokens = Tokenizer.TokenizeToList(text.Text).Tokens.Where(t => t.Kind != TokenKind.CjkSingle).ToList();
        if (tokens.Count < 10)
        {
            continue;
        }

        if (andQueries.Count < count)
        {
            andQueries.Add($"{tokens[random.Next(tokens.Count)].Text} {tokens[random.Next(tokens.Count)].Text}");
        }

        if (phraseQueries.Count < count)
        {
            var start = random.Next(tokens.Count - 3);
            var length = random.Next(2, 4);
            var run = tokens.Skip(start).Take(length).ToList();
            if (run.Zip(run.Skip(1)).All(p => p.Second.Position == p.First.Position + 1))
            {
                phraseQueries.Add("\"" + string.Join(" ", run.Select(t => t.Text)) + "\"");
            }
        }
    }

    return (andQueries, phraseQueries);
}

/// <summary>Runs the real indexing service with FileSystemWatcher and times new/modified files until they are found.</summary>
static async Task<List<double>> MeasureVisibilityAsync(IndexWriter writer, PhysicalSourceFileSystem fileSystem, ContentExtractor extractor, string root)
{
    var diagnostics = new DiagnosticsLog(TimeProvider.System);
    var engine = new IndexingEngine(writer, fileSystem, extractor, diagnostics, TimeProvider.System, new IndexingOptions { Roots = [root] });
    await using var service = new IndexingService(engine, writer, new PhysicalFileWatcherFactory(), fileSystem, diagnostics, TimeProvider.System, TimeSpan.FromMinutes(5));
    service.Start();
    while (service.Status.Pending > 0 || service.Status.Busy || engine.ProcessedCount == 0)
    {
        await Task.Delay(50);
    }

    var folder = Path.Combine(root, "perf-live");
    Directory.CreateDirectory(folder);
    var latencies = new List<double>();
    try
    {
        for (var i = 0; i < 10; i++)
        {
            var token = $"zzlive{i}x{Environment.TickCount64}";
            var path = Path.Combine(folder, i % 2 == 0 ? $"new{i}.txt" : "edited.txt");
            var timer = Stopwatch.StartNew();
            await File.WriteAllTextAsync(path, $"即時更新測試 {token} 內容");
            var query = QueryBinder.Bind(QueryParser.Parse(token));
            while (true)
            {
                using (var snapshot = writer.AcquireSnapshot())
                {
                    if (QueryExecutor.Execute(snapshot, query, SearchOptions.Default).TotalHits > 0)
                    {
                        break;
                    }
                }

                if (timer.Elapsed > TimeSpan.FromSeconds(30))
                {
                    throw new TimeoutException($"{path} never became searchable.");
                }

                await Task.Delay(20);
            }

            latencies.Add(timer.Elapsed.TotalMilliseconds);
            await Task.Delay(300);
        }
    }
    finally
    {
        Directory.Delete(folder, recursive: true);
    }

    return latencies;
}

static string CpuName()
{
    try
    {
        return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "")?.ToString()?.Trim() ?? "";
    }
    catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
    {
        return "unknown";
    }
}

static double TotalMemoryGb() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024;

internal sealed record PerfOptions(string Corpus, string Work, int Threads, int Queries, int Cores, bool SkipBuild)
{
    public static PerfOptions? Parse(string[] args)
    {
        string? corpus = null, work = null;
        int threads = 8, queries = 300, cores = 16;
        var skipBuild = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--corpus": corpus = args[++i]; break;
                case "--work": work = args[++i]; break;
                case "--threads": threads = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--queries": queries = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cores": cores = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--skip-build": skipBuild = true; break;
                default: return null;
            }
        }

        return corpus is null || work is null ? null : new PerfOptions(Path.GetFullPath(corpus), Path.GetFullPath(work), threads, queries, cores, skipBuild);
    }
}

/// <summary>Samples the working set every 50 ms; PeakWorkingSet64 alone misses nothing but is process-lifetime.</summary>
internal sealed class PeakSampler
{
    private readonly Process _process;
    private readonly Timer _timer;
    private long _peak;

    public PeakSampler(Process process)
    {
        _process = process;
        _timer = new Timer(_ => Sample(), null, 0, 50);
    }

    public double PeakMegabytes => Math.Max(_peak, _process.PeakWorkingSet64) / 1024.0 / 1024;

    public void Stop()
    {
        _timer.Dispose();
        Sample();
    }

    private void Sample()
    {
        _process.Refresh();
        long current = _process.WorkingSet64, seen;
        do
        {
            seen = Interlocked.Read(ref _peak);
        }
        while (current > seen && Interlocked.CompareExchange(ref _peak, current, seen) != seen);
    }
}
