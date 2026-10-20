using LocalSeek.Core.Index;
using LocalSeek.Core.Query;
using LocalSeek.Core.Search;

namespace LocalSeek.Tests.Search;

public class QueryExecutorTests
{
    private static readonly DateTime Jan1 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MemoryIndex Index(params (string Path, string Body)[] docs) =>
        MemoryIndex.Build(docs.Select(d => new DocumentInput(d.Path, 100, Jan1, d.Body)));

    private static SearchResult Search(IIndexReader index, string query, int topK = 200, CancellationToken token = default) =>
        QueryExecutor.Execute(index, QueryBinder.Bind(QueryParser.Parse(query)), new SearchOptions(topK, TimeZoneInfo.Utc), token);

    [Fact]
    public void Score_follows_the_bm25_variant()
    {
        // Names "a.txt", "b.txt", "c.txt" have 2 tokens each, weighted ×2.
        // len = 2+4 = 6, 1+4 = 5, 1+4 = 5; N = 3; avgdl = 16/3; df(apple) = 2.
        var index = Index((@"C:\x\a.txt", "apple banana"), (@"C:\x\b.txt", "apple"), (@"C:\x\c.txt", "cherry"));

        var result = Search(index, "apple");

        var idf = Math.Log(1 + ((3 - 2 + 0.5) / (2 + 0.5)));
        var avgdl = 16.0 / 3;
        double Expected(int len) => idf * 1 * 2.2 / (1 + (1.2 * (0.25 + (0.75 * len / avgdl))));
        Assert.Equal(2, result.TotalHits);
        Assert.Equal([@"C:\x\b.txt", @"C:\x\a.txt"], result.Hits.Select(h => h.Path));
        Assert.Equal(Expected(5), result.Hits[0].Score, 12);
        Assert.Equal(Expected(6), result.Hits[1].Score, 12);
        Assert.Equal(16, index.TotalLength);
        Assert.Equal(new TermStatistics("term(apple)", 2, idf), result.Terms.Single());
    }

    [Fact]
    public void File_name_hits_count_twice()
    {
        var index = Index((@"C:\x\apple.txt", ""), (@"C:\x\other.txt", "apple"));

        var tf = QueryExecutor.TermFrequencies(index, new TermQuery("apple"));

        Assert.Equal([2, 1], tf);
        Assert.Equal(4, index.GetLength(0));
        Assert.Equal(5, index.GetLength(1));
    }

    [Fact]
    public void Single_char_query_uses_char_frequency_including_bigram_runs()
    {
        var index = Index((@"C:\x\1.txt", "台灣台 台"), (@"C:\x\台.txt", "無"));

        var tf = QueryExecutor.TermFrequencies(index, new CharQuery("台"));

        Assert.Equal([3, 2], tf);
    }

    [Fact]
    public void Phrase_counts_start_positions_and_does_not_cross_cjk_punctuation()
    {
        var index = Index((@"C:\x\1.txt", "AI模型 ai 模型，AI。模型"), (@"C:\x\2.txt", "台灣，大學 台灣大學"));

        Assert.Equal([3, 0], QueryExecutor.TermFrequencies(index, new PhraseQuery(["ai", "模型"])));
        Assert.Equal([0, 1], QueryExecutor.TermFrequencies(index, new PhraseQuery(["台灣", "灣大", "大學"])));
    }

    [Fact]
    public void Prefix_sums_all_matching_latin_terms()
    {
        var index = Index((@"C:\x\1.txt", "apple apply banana ap"), (@"C:\x\apples.txt", "x"));

        Assert.Equal([2, 2], QueryExecutor.TermFrequencies(index, new PrefixQuery("app")));
    }

    [Fact]
    public void Filter_only_query_scores_zero_and_orders_by_path_ignoring_case()
    {
        var index = Index((@"C:\x\b.txt", "x"), (@"c:\X\A.txt", "y"), (@"C:\x\c.md", "z"));

        var result = Search(index, "ext:txt");

        Assert.Equal([@"c:\X\A.txt", @"C:\x\b.txt"], result.Hits.Select(h => h.Path));
        Assert.All(result.Hits, h => Assert.Equal(0, h.Score));
        Assert.Empty(result.Terms);
    }

    [Fact]
    public void Not_excludes_documents_and_is_not_scored()
    {
        var index = Index((@"C:\x\1.txt", "apple banana"), (@"C:\x\2.txt", "apple"));

        var withNot = Search(index, "apple -banana");
        var plain = Search(index, "apple");

        Assert.Equal([@"C:\x\2.txt"], withNot.Hits.Select(h => h.Path));
        Assert.Equal(plain.Hits.Single(h => h.Path == @"C:\x\2.txt").Score, withNot.Hits[0].Score);
    }

    [Fact]
    public void Repeated_identical_terms_are_scored_once()
    {
        var index = Index((@"C:\x\1.txt", "apple banana"), (@"C:\x\2.txt", "apple"));

        Assert.Equal(Search(index, "apple").Hits, Search(index, "apple apple \"apple\"").Hits);
    }

    [Fact]
    public void Top_k_limits_hits_but_not_total()
    {
        var index = Index(Enumerable.Range(0, 50).Select(i => ($@"C:\x\{i:D2}.txt", "apple")).ToArray());

        var result = Search(index, "apple", topK: 5);

        Assert.Equal(50, result.TotalHits);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $@"C:\x\{i:D2}.txt"), result.Hits.Select(h => h.Path));
    }

    [Fact]
    public void Empty_index_returns_no_hits()
    {
        var result = Search(new MemoryIndex(), "apple");

        Assert.Equal(0, result.TotalHits);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public void Cancelled_token_stops_execution()
    {
        var index = Index((@"C:\x\1.txt", "apple"));

        Assert.ThrowsAny<OperationCanceledException>(() => Search(index, "apple", token: new CancellationToken(true)));
    }
}
