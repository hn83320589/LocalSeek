using LocalSeek.Core.Query;

namespace LocalSeek.Tests.Query;

public class QueryParserTests
{
    [Theory]
    [InlineData("apple", "word(apple)")]
    [InlineData("apple banana", "(AND word(apple) word(banana))")]
    [InlineData("apple AND banana", "(AND word(apple) word(banana))")]
    [InlineData("apple OR banana", "(OR word(apple) word(banana))")]
    [InlineData("NOT apple", "(NOT word(apple))")]
    [InlineData("-apple", "(NOT word(apple))")]
    [InlineData("a -b", "(AND word(a) (NOT word(b)))")]
    [InlineData("\"big apple\"", "phrase(big apple)")]
    [InlineData("abc*", "prefix(abc)")]
    [InlineData("(a)", "word(a)")]
    [InlineData("((a))", "word(a)")]
    public void Parses_basic_forms(string query, string expected)
    {
        Assert.Equal(expected, QueryParser.Parse(query).ToString());
    }

    [Theory]
    // NOT binds tighter than AND, AND tighter than OR.
    [InlineData("a OR b c", "(OR word(a) (AND word(b) word(c)))")]
    [InlineData("a b OR c", "(OR (AND word(a) word(b)) word(c))")]
    [InlineData("a AND b OR c AND d", "(OR (AND word(a) word(b)) (AND word(c) word(d)))")]
    [InlineData("NOT a OR b", "(OR (NOT word(a)) word(b))")]
    [InlineData("NOT a b", "(AND (NOT word(a)) word(b))")]
    [InlineData("a OR b OR c", "(OR word(a) word(b) word(c))")]
    [InlineData("(a OR b) c", "(AND (OR word(a) word(b)) word(c))")]
    [InlineData("a (b OR (c -d))", "(AND word(a) (OR word(b) (AND word(c) (NOT word(d)))))")]
    [InlineData("NOT NOT a", "(NOT (NOT word(a)))")]
    [InlineData("-(a OR b) c", "(AND (NOT (OR word(a) word(b))) word(c))")]
    [InlineData("-\"x y\" z", "(AND (NOT phrase(x y)) word(z))")]
    public void Respects_operator_precedence_and_parentheses(string query, string expected)
    {
        Assert.Equal(expected, QueryParser.Parse(query).ToString());
    }

    [Theory]
    // Operators are case-sensitive: only uppercase words are operators.
    [InlineData("a and b", "(AND word(a) word(and) word(b))")]
    [InlineData("a or b", "(AND word(a) word(or) word(b))")]
    [InlineData("not a", "(AND word(not) word(a))")]
    [InlineData("a-b", "word(a-b)")]
    [InlineData("a - b", "(AND word(a) word(-) word(b))")]
    [InlineData("abc*def", "word(abc*def)")]
    [InlineData("foo:bar", "word(foo:bar)")]
    [InlineData("  a\t\tb  ", "(AND word(a) word(b))")]
    [InlineData("台灣(大學)", "(AND word(台灣) word(大學))")]
    public void Treats_non_operator_text_as_words(string query, string expected)
    {
        Assert.Equal(expected, QueryParser.Parse(query).ToString());
    }

    [Theory]
    [InlineData("ext:txt", "ext:txt")]
    [InlineData("ext:.TXT", "ext:txt")]
    [InlineData("EXT:Md", "ext:md")]
    [InlineData("path:docs", "path:docs")]
    [InlineData("path:\"C:\\My Docs\"", "path:C:\\My Docs")]
    [InlineData("modified:2024-01-31", "modified:=2024-01-31")]
    [InlineData("modified:=2024-01-31", "modified:=2024-01-31")]
    [InlineData("modified:>=2024-01-31", "modified:>=2024-01-31")]
    [InlineData("modified:<=2024-01-31", "modified:<=2024-01-31")]
    [InlineData("size:>10", "size:>10")]
    [InlineData("size:<10KB", "size:<10240")]
    [InlineData("size:>2mb", "size:>2097152")]
    [InlineData("-ext:log", "(NOT ext:log)")]
    [InlineData("report ext:docx", "(AND word(report) ext:docx)")]
    public void Parses_filters(string query, string expected)
    {
        Assert.Equal(expected, QueryParser.Parse(query).ToString());
    }

    [Theory]
    [InlineData("", 0, "查詢是空的")]
    [InlineData("   ", 0, "查詢是空的")]
    [InlineData("a AND", 2, "AND 後面缺少條件")]
    [InlineData("a OR", 2, "OR 後面缺少條件")]
    [InlineData("NOT", 0, "NOT 後面缺少條件")]
    [InlineData("AND a", 0, "AND 前面缺少條件")]
    [InlineData("OR a", 0, "OR 前面缺少條件")]
    [InlineData("a AND OR b", 2, "AND 後面缺少條件")]
    [InlineData("a OR AND b", 2, "OR 後面缺少條件")]
    [InlineData("a (OR b)", 3, "OR 前面缺少條件")]
    [InlineData("(a", 0, "缺少右括號")]
    [InlineData("a)", 1, "多餘的右括號")]
    [InlineData("()", 0, "括號內沒有內容")]
    [InlineData("a \"b c", 2, "引號沒有結束")]
    [InlineData("ext:", 0, "ext: 需要副檔名")]
    [InlineData("path:", 0, "path: 需要路徑片段")]
    [InlineData("modified:2024-13-01", 9, "日期格式必須是 yyyy-MM-dd")]
    [InlineData("modified:>2024-01-01", 9, "modified: 只支援 >=、<=、=")]
    [InlineData("modified:", 0, "modified: 需要日期")]
    [InlineData("size:10", 5, "size: 必須以 < 或 > 開頭")]
    [InlineData("size:>", 6, "size: 需要數字")]
    [InlineData("size:>10GB", 8, "size: 單位只支援 KB 或 MB")]
    [InlineData("size:>99999999999999999999", 6, "size: 數值過大")]
    [InlineData("x path:\"abc", 7, "引號沒有結束")]
    public void Reports_syntax_errors_with_position(string query, int position, string message)
    {
        var ex = Assert.Throws<QuerySyntaxException>(() => QueryParser.Parse(query));

        Assert.Equal(position, ex.Position);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_length_covers_offending_token()
    {
        var ex = Assert.Throws<QuerySyntaxException>(() => QueryParser.Parse("a AND OR b"));

        Assert.Equal(3, ex.Length);
    }

    [Fact]
    public void Node_positions_point_into_query()
    {
        var node = (AndNode)QueryParser.Parse("ab  \"cd ef\"");

        Assert.Equal((0, 2), (node.Children[0].Start, node.Children[0].Length));
        Assert.Equal((4, 7), (node.Children[1].Start, node.Children[1].Length));
    }
}
