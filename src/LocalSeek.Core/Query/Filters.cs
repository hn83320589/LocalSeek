namespace LocalSeek.Core.Query;

/// <summary>The per-document facts filters are evaluated against.</summary>
public readonly record struct DocumentMetadata(string Path, long Size, DateTime LastWriteUtc);

/// <summary>A non-scoring condition on document metadata.</summary>
public abstract record Filter
{
    public abstract bool Matches(in DocumentMetadata document, TimeZoneInfo timeZone);
}

/// <summary>ext: — extension without the dot, compared case-insensitively.</summary>
public sealed record ExtensionFilter(string Extension) : Filter
{
    public override bool Matches(in DocumentMetadata document, TimeZoneInfo timeZone) =>
        GetExtension(document.Path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"ext:{Extension}";

    public static ReadOnlySpan<char> GetExtension(string path)
    {
        var span = path.AsSpan();
        var name = span[(span.LastIndexOfAny('\\', '/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot < 0 ? [] : name[(dot + 1)..];
    }
}

/// <summary>path: — case-insensitive substring of the full path; '/' and '\' are interchangeable.</summary>
public sealed record PathFilter(string Fragment) : Filter
{
    private readonly string _normalizedFragment = Fragment.Replace('/', '\\');

    public override bool Matches(in DocumentMetadata document, TimeZoneInfo timeZone) =>
        document.Path.Replace('/', '\\').Contains(_normalizedFragment, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"path:{Fragment}";
}

public enum DateComparison
{
    Equal,
    AtOrAfter,
    AtOrBefore,
}

/// <summary>modified: — compares the local calendar date of the last write time.</summary>
public sealed record ModifiedFilter(DateComparison Comparison, DateOnly Date) : Filter
{
    public override bool Matches(in DocumentMetadata document, TimeZoneInfo timeZone)
    {
        var utc = DateTime.SpecifyKind(document.LastWriteUtc, DateTimeKind.Utc);
        var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone));
        return Comparison switch
        {
            DateComparison.Equal => local == Date,
            DateComparison.AtOrAfter => local >= Date,
            DateComparison.AtOrBefore => local <= Date,
            _ => throw new InvalidOperationException($"Unknown comparison {Comparison}."),
        };
    }

    public override string ToString()
    {
        var op = Comparison switch
        {
            DateComparison.AtOrAfter => ">=",
            DateComparison.AtOrBefore => "<=",
            _ => "=",
        };
        return $"modified:{op}{Date:yyyy-MM-dd}";
    }
}

public enum SizeComparison
{
    LessThan,
    GreaterThan,
}

/// <summary>size: — strict comparison against a byte count (KB/MB already expanded, base 1024).</summary>
public sealed record SizeFilter(SizeComparison Comparison, long Bytes) : Filter
{
    public override bool Matches(in DocumentMetadata document, TimeZoneInfo timeZone) =>
        Comparison == SizeComparison.LessThan ? document.Size < Bytes : document.Size > Bytes;

    public override string ToString() => $"size:{(Comparison == SizeComparison.LessThan ? "<" : ">")}{Bytes}";
}
