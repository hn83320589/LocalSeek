using LocalSeek.Core.Query;

namespace LocalSeek.Tests.Query;

public class QueryBinderTests
{
    private static string Bind(string query) => QueryBinder.Bind(QueryParser.Parse(query)).ToString();

    [Theory]
    [InlineData("Apple", "term(apple)")]
    [InlineData("ＡＰＰＬＥ", "term(apple)")]
    [InlineData("台灣", "term(台灣)")]
    [InlineData("臺灣", "term(台灣)")]
    [InlineData("台", "char(台)")]
    [InlineData("𠮷", "char(𠮷)")]
    [InlineData("台灣大學", "phrase(台灣 灣大 大學)")]
    [InlineData("AI模型", "phrase(ai 模型)")]
    [InlineData("e-mail", "phrase(e mail)")]
    [InlineData("A模B", "(AND term(a) char(模) term(b))")]
    [InlineData("台,灣", "(AND char(台) char(灣))")]
    [InlineData("\"big apple\"", "phrase(big apple)")]
    [InlineData("\"apple\"", "term(apple)")]
    [InlineData("\"台灣，大學\"", "phrase(台灣 大學)")]
    [InlineData("ab*", "prefix(ab)")]
    [InlineData("ＡＢＣ*", "prefix(abc)")]
    public void Binds_words_phrases_and_prefixes(string query, string expected)
    {
        Assert.Equal(expected, Bind(query));
    }

    [Theory]
    [InlineData("apple !!!", "term(apple)")]
    [InlineData("apple OR ，，", "term(apple)")]
    [InlineData("apple (!!! OR ...)", "term(apple)")]
    [InlineData("apple -!!!", "term(apple)")]
    [InlineData("a - b", "(AND term(a) term(b))")]
    [InlineData("!!! ext:txt", "ext:txt")]
    public void Ignores_words_without_tokens(string query, string expected)
    {
        Assert.Equal(expected, Bind(query));
    }

    [Theory]
    [InlineData("a -b", "(AND term(a) (NOT term(b)))")]
    [InlineData("ext:txt", "ext:txt")]
    [InlineData("ext:txt -path:tmp", "(AND ext:txt (NOT path:tmp))")]
    [InlineData("a OR (b -c)", "(OR term(a) (AND term(b) (NOT term(c))))")]
    [InlineData("NOT NOT a", "(NOT (NOT term(a)))")]
    [InlineData("NOT (-a OR -b)", "(NOT (OR (NOT term(a)) (NOT term(b))))")]
    public void Accepts_bounded_queries(string query, string expected)
    {
        Assert.Equal(expected, Bind(query));
    }

    [Theory]
    [InlineData("-a")]
    [InlineData("NOT a")]
    [InlineData("-a -b")]
    [InlineData("-ext:txt")]
    [InlineData("a OR -b")]
    [InlineData("(-a) OR b")]
    [InlineData("-(a b)")]
    [InlineData("NOT (-a b)")]
    public void Rejects_queries_with_only_negative_conditions(string query)
    {
        var ex = Assert.Throws<QuerySyntaxException>(() => QueryBinder.Bind(QueryParser.Parse(query)));

        Assert.Contains("不可只有排除條件", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("!!!", "查詢沒有可用的詞項或篩選條件")]
    [InlineData("\"台 灣\"", "片語不可包含單字詞元")]
    [InlineData("\"AI模 x\"", "片語不可包含單字詞元")]
    [InlineData("a*", "前綴至少需要 2 個 Latin 字元")]
    [InlineData("*", "前綴至少需要 2 個 Latin 字元")]
    [InlineData("台灣*", "前綴至少需要 2 個 Latin 字元")]
    [InlineData("a-b*", "前綴至少需要 2 個 Latin 字元")]
    public void Reports_binding_errors(string query, string message)
    {
        var ex = Assert.Throws<QuerySyntaxException>(() => QueryBinder.Bind(QueryParser.Parse(query)));

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_error_reports_position_of_offending_node()
    {
        var ex = Assert.Throws<QuerySyntaxException>(() => QueryBinder.Bind(QueryParser.Parse("ok \"台 灣\"")));

        Assert.Equal(3, ex.Position);
        Assert.Equal(5, ex.Length);
    }
}
