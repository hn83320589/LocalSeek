using System.Diagnostics;
using System.Text;
using LocalSeek.App.ViewModels;
using LocalSeek.Core.Index;

namespace LocalSeek.Tests.ViewModels;

/// <summary>Acceptance #20: a cancelled search stops within 50 ms (real executor, large index).</summary>
public class CancellationLatencyTests
{
    private static readonly Lazy<MemoryIndex> Index = new(() =>
    {
        var random = new Random(4242);
        var words = Enumerable.Range(0, 40).Select(i => $"w{i}").ToArray();
        var docs = Enumerable.Range(0, 20_000).Select(i =>
        {
            var body = new StringBuilder();
            for (var k = 0; k < 80; k++)
            {
                body.Append(words[random.Next(words.Length)]).Append(' ');
            }

            return new DocumentInput($@"C:\perf\{i}.txt", 100, DateTime.UnixEpoch, body.ToString());
        });
        return MemoryIndex.Build(docs);
    });

    /// <summary>Many common phrases: slow enough that cancellation lands mid-execution.</summary>
    private static readonly string HeavyQuery = string.Join(" OR ", Enumerable.Range(0, 30).Select(i => $"\"w{i} w{(i + 1) % 40} w{(i + 2) % 40}\""));

    [Fact]
    public async Task Cancellation_takes_effect_within_50_ms()
    {
        var service = new IndexSearchService(() => Index.Value, TimeZoneInfo.Utc);
        var baseline = Stopwatch.StartNew();
        await service.SearchAsync(HeavyQuery, CancellationToken.None);
        baseline.Stop();
        Assert.True(baseline.ElapsedMilliseconds > 100, $"Query too fast ({baseline.ElapsedMilliseconds} ms) to measure cancellation.");

        var latencies = new List<long>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var cancellation = new CancellationTokenSource();
            var search = service.SearchAsync(HeavyQuery, cancellation.Token);
            await Task.Delay(30);
            var stopwatch = Stopwatch.StartNew();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
            latencies.Add(stopwatch.ElapsedMilliseconds);
        }

        // The median guards against a single scheduler hiccup on a busy CI machine.
        latencies.Sort();
        Assert.True(latencies[2] <= 50, $"Cancellation latencies: {string.Join(", ", latencies)} ms");
    }
}
