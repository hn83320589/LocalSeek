namespace LocalSeek.Core.Index;

/// <summary>
/// Tiered merge policy. A segment's level is 0 up to <see cref="MinSegmentDocs"/> live documents and
/// grows by one for every factor of <see cref="MergeFactor"/> beyond that. When a level holds
/// <see cref="MergeFactor"/> segments they are merged into one segment of the next level, so the
/// segment count stays below MergeFactor × (number of levels) = O(log N).
/// A segment whose deleted fraction reaches <see cref="ExpungeDeletesRatio"/> is rewritten alone.
/// </summary>
public sealed record MergePolicy
{
    public int MergeFactor { get; init; } = 10;

    public int MinSegmentDocs { get; init; } = 1000;

    public double ExpungeDeletesRatio { get; init; } = 0.3;

    public int LevelOf(int liveDocs) =>
        liveDocs <= MinSegmentDocs ? 0 : (int)Math.Floor(Math.Log((double)liveDocs / MinSegmentDocs, MergeFactor)) + 1;

    /// <summary>Segment ids to merge, or null when nothing needs merging.</summary>
    public IReadOnlyList<long>? Select(IReadOnlyList<(long Id, int TotalDocs, int LiveDocs)> segments)
    {
        var crowdedLevel = segments
            .GroupBy(s => LevelOf(s.LiveDocs))
            .Where(g => g.Count() >= MergeFactor)
            .OrderBy(g => g.Key)
            .FirstOrDefault();
        if (crowdedLevel is not null)
        {
            return crowdedLevel.Select(s => s.Id).Order().ToList();
        }

        var wasteful = segments
            .Where(s => s.TotalDocs > 0 && (double)(s.TotalDocs - s.LiveDocs) / s.TotalDocs >= ExpungeDeletesRatio)
            .OrderByDescending(s => s.TotalDocs - s.LiveDocs)
            .FirstOrDefault();
        return wasteful.TotalDocs > 0 ? [wasteful.Id] : null;
    }
}
