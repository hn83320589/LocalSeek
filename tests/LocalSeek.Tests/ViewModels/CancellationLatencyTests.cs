using System.Diagnostics;
using System.Text;
using LocalSeek.App.ViewModels;
using LocalSeek.Core.Index;
using LocalSeek.Core.Query;

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

    /// <summary>Many common phrases: the first postings read leaves almost all of the work still to do.</summary>
    private static readonly string HeavyQuery = string.Join(" OR ", Enumerable.Range(0, 120).Select(i => $"\"w{i % 40} w{(i + 1 + (i / 40)) % 40} w{(i + 2) % 40}\""));

    [Fact]
    public async Task Cancellation_takes_effect_within_50_ms()
    {
        // JIT the query path first so the measurement covers only cancellation.
        await new IndexSearchService(() => Index.Value, TimeZoneInfo.Utc).SearchAsync(HeavyQuery, CancellationToken.None);

        var latencies = new List<long>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // Cancel only once the executor is reading postings, so the search is certainly running;
            // timing-based guesses of "still running" proved unreliable on CI runners.
            var reader = new StartSignallingReader(Index.Value);
            var service = new IndexSearchService(() => reader, TimeZoneInfo.Utc);
            using var cancellation = new CancellationTokenSource();
            var search = service.SearchAsync(HeavyQuery, cancellation.Token);

            Assert.True(reader.Started.Wait(TimeSpan.FromSeconds(10)), "The search never started reading the index.");
            var stopwatch = Stopwatch.StartNew();
            await cancellation.CancelAsync();
            var outcome = await Record.ExceptionAsync(() => search);
            Assert.True(outcome is OperationCanceledException, $"Search completed without observing cancellation ({stopwatch.ElapsedMilliseconds} ms after cancel).");
            latencies.Add(stopwatch.ElapsedMilliseconds);
        }

        // The median guards against a single scheduler hiccup on a busy CI machine.
        latencies.Sort();
        Assert.True(latencies[2] <= 50, $"Cancellation latencies: {string.Join(", ", latencies)} ms");
    }

    /// <summary>Delegates to the real index and signals the first postings read.</summary>
    private sealed class StartSignallingReader(IIndexReader inner) : IIndexReader
    {
        public ManualResetEventSlim Started { get; } = new();

        public int DocCount => inner.DocCount;

        public long TotalLength => inner.TotalLength;

        public int GetLength(int doc) => inner.GetLength(doc);

        public DocumentMetadata GetMetadata(int doc) => inner.GetMetadata(doc);

        public void ForEachPosting(TermSpace space, string term, PostingCallback callback)
        {
            Started.Set();
            inner.ForEachPosting(space, term, callback);
        }

        public void ForEachPositions(TermSpace space, string term, PositionsCallback callback, Func<int, bool>? wanted = null)
        {
            Started.Set();
            inner.ForEachPositions(space, term, callback, wanted);
        }

        public IReadOnlyList<string> GetTermsWithPrefix(TermSpace space, string prefix) => inner.GetTermsWithPrefix(space, prefix);
    }
}
