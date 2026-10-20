using LocalSeek.Core.Query;
using LocalSeek.Core.Search;

namespace LocalSeek.Tests.Search;

public class SnippetTests
{
    private static BoundNode Q(string query) => QueryBinder.Bind(QueryParser.Parse(query));

    private static List<(int, int)> HighlightRanges(string text, string query) =>
        SnippetGenerator.Create(text, Q(query)).Highlights.Select(h => (h.Start, h.Length)).ToList();

    [Theory]
    [InlineData("報告：台灣大學", "台灣", 3, 2)]
    [InlineData("㈱台灣", "台灣", 1, 2)]
    [InlineData("ＡＩ模型", "ai", 0, 2)]
    [InlineData("the ﬁle", "file", 4, 3)]
    [InlineData("𠮷野家", "𠮷野", 0, 3)]
    [InlineData("AI 模型 x", "AI模型", 0, 5)]
    [InlineData("apples apply", "app*", 0, 6)]
    public void Highlights_use_original_text_coordinates(string text, string query, int start, int length)
    {
        Assert.Equal((start, length), HighlightRanges(text, query)[0]);
    }

    [Fact]
    public void Single_char_query_highlights_every_occurrence_including_inside_runs()
    {
        Assert.Equal([(0, 1), (2, 1), (5, 1)], HighlightRanges("台灣台 x台", "台"));
    }

    [Fact]
    public void Overlapping_and_adjacent_hits_are_merged()
    {
        var result = SnippetGenerator.Create("台灣大學", Q("台灣 灣大 大學"));

        Assert.Equal(3, result.Hits.Count);
        Assert.Equal([(0, 4)], result.Highlights.Select(h => (h.Start, h.Length)));
    }

    [Fact]
    public void Negated_terms_are_not_highlighted()
    {
        Assert.Equal([(0, 5)], HighlightRanges("apple banana", "apple -banana"));
    }

    [Fact]
    public void No_hits_gives_one_leading_snippet()
    {
        var text = new string('x', 300);

        var snippet = Assert.Single(SnippetGenerator.Create(text, Q("ext:txt")).Snippets);

        Assert.Equal(0, snippet.Start);
        Assert.Equal(SnippetGenerator.MaxLength, snippet.Text.Length);
        Assert.Empty(snippet.Highlights);
    }

    [Fact]
    public void At_most_three_snippets_in_document_order_each_at_most_120_chars()
    {
        var filler = new string('.', 200);
        var text = $"alpha{filler}beta{filler}gamma{filler}delta{filler}alpha";

        var result = SnippetGenerator.Create(text, Q("alpha OR beta OR gamma OR delta"));

        Assert.Equal(3, result.Snippets.Count);
        Assert.All(result.Snippets, s => Assert.True(s.Text.Length <= 120));
        Assert.Equal(result.Snippets.OrderBy(s => s.Start), result.Snippets);
        Assert.True(result.Snippets.Zip(result.Snippets.Skip(1)).All(p => p.First.Start + p.First.Text.Length <= p.Second.Start));
    }

    [Fact]
    public void Snippets_prefer_windows_covering_more_distinct_terms()
    {
        var filler = new string(' ', 150);
        var text = $"apple{filler}apple{filler}apple banana cherry{filler}";

        var first = SnippetGenerator.Create(text, Q("apple banana cherry"), maxSnippets: 1).Snippets.Single();

        Assert.Contains("banana", first.Text, StringComparison.Ordinal);
        Assert.Contains("cherry", first.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Snippet_text_is_the_source_substring_with_control_characters_blanked()
    {
        var text = "line one\nline\ttwo apple\r\nend";

        var snippet = SnippetGenerator.Create(text, Q("apple")).Snippets.Single();

        Assert.Equal(text.Substring(snippet.Start, snippet.Text.Length).Replace('\n', ' ').Replace('\t', ' ').Replace('\r', ' '), snippet.Text);
        var highlight = Assert.Single(snippet.Highlights);
        Assert.Equal("apple", snippet.Text.Substring(highlight.Start, highlight.Length));
    }

    [Fact]
    public void Window_edges_never_split_a_surrogate_pair()
    {
        // Put a surrogate pair exactly where a 120-char window would end.
        var text = "apple" + new string('x', 114) + "𠮷" + new string('y', 200);
        Assert.True(char.IsHighSurrogate(text[119]));

        var snippet = SnippetGenerator.Create(text, Q("apple")).Snippets.Single();
        var end = snippet.Start + snippet.Text.Length;

        Assert.False(char.IsHighSurrogate(text[end - 1]));
        Assert.True(snippet.Text.Length <= 120);
    }

    [Fact]
    public void Highlight_longer_than_the_window_is_clipped()
    {
        var word = new string('a', 150);

        var snippet = SnippetGenerator.Create(word, Q(word)).Snippets.Single();

        Assert.Equal(120, snippet.Text.Length);
        Assert.Equal((0, 120), (snippet.Highlights[0].Start, snippet.Highlights[0].Length));
    }

    [Fact]
    public void Empty_text_gives_no_snippets()
    {
        Assert.Empty(SnippetGenerator.Create("", Q("apple")).Snippets);
    }
}
