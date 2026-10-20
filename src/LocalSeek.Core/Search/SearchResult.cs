namespace LocalSeek.Core.Search;

public sealed record SearchOptions(int TopK = 200, TimeZoneInfo? TimeZone = null)
{
    public static SearchOptions Default { get; } = new();
}

public sealed record SearchHit(int Doc, string Path, double Score);

/// <summary>Statistics of one scoring term, exposed for diagnostics and hand-calculated examples.</summary>
public sealed record TermStatistics(string Key, int DocumentFrequency, double Idf);

public sealed record SearchResult(
    int TotalHits,
    IReadOnlyList<SearchHit> Hits,
    IReadOnlyList<TermStatistics> Terms,
    int DocumentCount,
    double AverageLength);
