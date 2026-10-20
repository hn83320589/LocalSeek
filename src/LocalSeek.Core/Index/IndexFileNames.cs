using System.Globalization;

namespace LocalSeek.Core.Index;

/// <summary>File naming for everything in the index directory.</summary>
public static class IndexFileNames
{
    public const string TempSuffix = ".tmp";

    public static string Segment(long id) => $"seg_{id:D8}.lss";

    public static string Deletions(long segmentId, long generation) => $"seg_{segmentId:D8}_{generation:D6}.lsd";

    public static string Manifest(long generation) => $"manifest_{generation:D10}.lsm";

    public static bool TryParseManifest(string name, out long generation) =>
        TryParseNumber(name, "manifest_", ".lsm", out generation);

    public static bool TryParseSegment(string name, out long id) => TryParseNumber(name, "seg_", ".lss", out id);

    /// <summary>Segment id embedded in a segment or deletion file name.</summary>
    public static bool TryParseSegmentIdOfAny(string name, out long id)
    {
        id = 0;
        return name.StartsWith("seg_", StringComparison.Ordinal)
            && name.Length >= 12
            && long.TryParse(name.AsSpan(4, 8), NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private static bool TryParseNumber(string name, string prefix, string suffix, out long value)
    {
        value = 0;
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && name.EndsWith(suffix, StringComparison.Ordinal)
            && long.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
