namespace LocalSeek.Core.Query;

/// <summary>A query that cannot be parsed or bound. Position/Length locate the problem in the query string.</summary>
public sealed class QuerySyntaxException(string message, int position, int length)
    : Exception($"{message}（位置 {position}）")
{
    public string Reason { get; } = message;

    public int Position { get; } = position;

    public int Length { get; } = length;
}
