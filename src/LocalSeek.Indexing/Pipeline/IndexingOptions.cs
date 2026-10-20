using LocalSeek.Indexing.Crawling;
using LocalSeek.Indexing.Settings;

namespace LocalSeek.Indexing.Pipeline;

public sealed record IndexingOptions
{
    public IReadOnlyList<string> Roots { get; init; } = [];

    public ExclusionRules Exclusions { get; init; } = ExclusionRules.None;

    public CrawlOptions Crawl { get; init; } = new();

    /// <summary>A path is processed once it has been quiet this long…</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>…but never later than this after its first event, even if it keeps changing.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Back-off for files locked by another program; after the last one the file is given up until it changes.</summary>
    public IReadOnlyList<TimeSpan> LockRetryDelays { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    public int MaxParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Documents added between intermediate commits during large batches (initial build).</summary>
    public int CommitInterval { get; init; } = 5000;

    public static IndexingOptions From(AppSettings settings) => new()
    {
        Roots = settings.Roots.Select(r => r.TrimEnd('\\', '/')).ToList(),
        Exclusions = new ExclusionRules(settings.ExcludePatterns),
        Crawl = new CrawlOptions(settings.SkipHiddenAndSystem, settings.FollowReparsePoints),
        Debounce = TimeSpan.FromMilliseconds(settings.DebounceMilliseconds),
        MaxDelay = TimeSpan.FromMilliseconds(Math.Max(settings.DebounceMilliseconds, 1500)),
    };
}
