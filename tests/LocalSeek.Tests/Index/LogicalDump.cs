using System.Text;
using LocalSeek.Core.Index;

namespace LocalSeek.Tests.Index;

/// <summary>
/// Canonical text form of everything an index exposes about its live documents: metadata, lengths and
/// every posting with positions. Two readers are logically identical iff their dumps are equal.
/// </summary>
public static class LogicalDump
{
    public static string Of(IIndexReader reader)
    {
        var lines = new List<string>();
        for (var d = 0; d < reader.DocCount; d++)
        {
            var m = reader.GetMetadata(d);
            lines.Add($"{m.Path}|doc|{m.Size}|{m.LastWriteUtc.Ticks}|{reader.GetLength(d)}");
        }

        foreach (var space in Enum.GetValues<TermSpace>())
        {
            foreach (var term in reader.GetTermsWithPrefix(space, ""))
            {
                if (space is TermSpace.BodyTerm or TermSpace.NameTerm)
                {
                    reader.ForEachPositions(space, term, (doc, positions) =>
                        lines.Add($"{reader.GetMetadata(doc).Path}|{space}|{term}|{string.Join(',', positions.ToArray())}"));
                }
                else
                {
                    reader.ForEachPosting(space, term, (doc, frequency) =>
                        lines.Add($"{reader.GetMetadata(doc).Path}|{space}|{term}|{frequency}"));
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        lines.ForEach(l => sb.AppendLine(l));
        return sb.ToString();
    }

    public static string Of(IndexWriter writer)
    {
        using var snapshot = writer.AcquireSnapshot();
        return Of(snapshot);
    }
}
