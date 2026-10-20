using LocalSeek.Core.Index;
using LocalSeek.Core.Query;
using LocalSeek.Core.Search;
using LocalSeek.Tests.Reference;

namespace LocalSeek.Tests.Properties;

/// <summary>Shared comparison of the engine against the reference engine.</summary>
public static class Oracle
{
    public static readonly TimeZoneInfo Utc8 = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    /// <summary>Binds with both binders; returns false when both reject the query, fails when they disagree.</summary>
    public static bool TryBindBoth(string query, out BoundNode engine, out BoundNode reference)
    {
        engine = reference = null!;
        QueryNode syntax;
        try
        {
            syntax = QueryParser.Parse(query);
        }
        catch (QuerySyntaxException)
        {
            return false;
        }

        string? engineError = null, referenceError = null;
        try
        {
            engine = QueryBinder.Bind(syntax);
        }
        catch (QuerySyntaxException ex)
        {
            engineError = ex.Message;
        }

        try
        {
            reference = ReferenceEngine.Bind(syntax);
        }
        catch (QuerySyntaxException ex)
        {
            referenceError = ex.Message;
        }

        Assert.True(
            (engineError is null) == (referenceError is null),
            $"Binders disagree on '{query}': engine={engineError ?? engine.ToString()} reference={referenceError ?? reference.ToString()}");
        return engineError is null;
    }

    /// <summary>Same total, same ranking (ties by path) and scores within 1e-9 relative error.</summary>
    public static void AssertSameResults(IIndexReader index, ReferenceEngine reference, string query, int topK = int.MaxValue)
    {
        if (!TryBindBoth(query, out var engineQuery, out var referenceQuery))
        {
            return;
        }

        var expected = reference.Search(referenceQuery, topK);
        var actual = QueryExecutor.Execute(index, engineQuery, new SearchOptions(topK, Utc8));

        Assert.Equal(reference.Count, actual.DocumentCount);
        Assert.Equal(expected.Total, actual.TotalHits);
        Assert.Equal(expected.Hits.Select(h => h.Path), actual.Hits.Select(h => h.Path));
        for (var i = 0; i < expected.Hits.Count; i++)
        {
            var e = expected.Hits[i].Score;
            var a = actual.Hits[i].Score;
            var error = e == 0 ? Math.Abs(a) : Math.Abs(a - e) / Math.Abs(e);
            Assert.True(error <= 1e-9, $"Score {a} vs reference {e} (relative error {error}) for '{query}'.");
        }
    }
}
