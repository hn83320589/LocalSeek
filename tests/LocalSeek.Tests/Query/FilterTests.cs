using LocalSeek.Core.Query;

namespace LocalSeek.Tests.Query;

public class FilterTests
{
    private static readonly TimeZoneInfo Utc8 = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    private static Filter ParseFilter(string query) => ((FilterNode)QueryParser.Parse(query)).Filter;

    private static DocumentMetadata Doc(string path = @"C:\data\Report.TXT", long size = 0, DateTime? modifiedUtc = null) =>
        new(path, size, modifiedUtc ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Theory]
    [InlineData("ext:txt", @"C:\a\b.txt", true)]
    [InlineData("ext:txt", @"C:\a\b.TXT", true)]
    [InlineData("ext:TXT", @"C:\a\b.txt", true)]
    [InlineData("ext:txt", @"C:\a\b.txt.bak", false)]
    [InlineData("ext:gz", @"C:\a\b.tar.gz", true)]
    [InlineData("ext:txt", @"C:\a\txt", false)]
    [InlineData("ext:txt", @"C:\a.txt\readme", false)]
    [InlineData("ext:tx", @"C:\a\b.txt", false)]
    public void Extension_filter(string query, string path, bool expected)
    {
        Assert.Equal(expected, ParseFilter(query).Matches(Doc(path), Utc8));
    }

    [Theory]
    [InlineData("path:data", @"C:\Data\x.txt", true)]
    [InlineData("path:DATA", @"C:\data\x.txt", true)]
    [InlineData("path:c:\\data\\x", @"C:\data\x.txt", true)]
    [InlineData("path:data/x", @"C:\data\x.txt", true)]
    [InlineData("path:x.txt", @"C:\data\x.txt", true)]
    [InlineData("path:datax", @"C:\data\x.txt", false)]
    [InlineData("path:\"my docs\"", @"D:\My Docs\a.md", true)]
    public void Path_filter_is_case_insensitive_substring(string query, string path, bool expected)
    {
        Assert.Equal(expected, ParseFilter(query).Matches(Doc(path), Utc8));
    }

    [Theory]
    // Boundary at exactly the limit and one byte either side; KB/MB are base 1024.
    [InlineData("size:<10", 9, true)]
    [InlineData("size:<10", 10, false)]
    [InlineData("size:>10", 10, false)]
    [InlineData("size:>10", 11, true)]
    [InlineData("size:<1KB", 1023, true)]
    [InlineData("size:<1KB", 1024, false)]
    [InlineData("size:>1KB", 1024, false)]
    [InlineData("size:>1KB", 1025, true)]
    [InlineData("size:>1MB", 1048576, false)]
    [InlineData("size:>1MB", 1048577, true)]
    [InlineData("size:<1mb", 1048575, true)]
    [InlineData("size:>0", 0, false)]
    public void Size_filter_boundaries(string query, long size, bool expected)
    {
        Assert.Equal(expected, ParseFilter(query).Matches(Doc(size: size), Utc8));
    }

    [Theory]
    // 2024-03-10 local (UTC+8) runs from 2024-03-09T16:00Z to 2024-03-10T16:00Z (exclusive).
    [InlineData("modified:2024-03-10", "2024-03-09T16:00:00Z", true)]
    [InlineData("modified:2024-03-10", "2024-03-09T15:59:59.9999999Z", false)]
    [InlineData("modified:2024-03-10", "2024-03-10T15:59:59.9999999Z", true)]
    [InlineData("modified:2024-03-10", "2024-03-10T16:00:00Z", false)]
    [InlineData("modified:>=2024-03-10", "2024-03-09T16:00:00Z", true)]
    [InlineData("modified:>=2024-03-10", "2024-03-09T15:59:59Z", false)]
    [InlineData("modified:<=2024-03-10", "2024-03-10T15:59:59Z", true)]
    [InlineData("modified:<=2024-03-10", "2024-03-10T16:00:00Z", false)]
    [InlineData("modified:=2024-03-11", "2024-03-10T16:00:00Z", true)]
    public void Modified_filter_compares_local_utc8_dates(string query, string utc, bool expected)
    {
        var modified = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

        Assert.Equal(expected, ParseFilter(query).Matches(Doc(modifiedUtc: modified), Utc8));
    }

    [Fact]
    public void Modified_filter_uses_the_injected_time_zone()
    {
        var filter = ParseFilter("modified:2024-03-10");
        var modified = new DateTime(2024, 3, 9, 20, 0, 0, DateTimeKind.Utc);

        Assert.True(filter.Matches(Doc(modifiedUtc: modified), Utc8));
        Assert.False(filter.Matches(Doc(modifiedUtc: modified), TimeZoneInfo.Utc));
    }
}
