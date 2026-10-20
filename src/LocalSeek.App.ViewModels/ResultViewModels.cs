using System.Globalization;
using LocalSeek.Core.Search;
using LocalSeek.Indexing.Preview;

namespace LocalSeek.App.ViewModels;

/// <summary>A piece of text that is or is not highlighted; the view turns segments into runs.</summary>
public sealed record TextSegment(string Text, bool IsHighlight)
{
    public static IReadOnlyList<TextSegment> Split(string text, IReadOnlyList<Highlight> highlights)
    {
        var segments = new List<TextSegment>();
        var position = 0;
        foreach (var h in highlights.OrderBy(h => h.Start))
        {
            if (h.Start > position)
            {
                segments.Add(new TextSegment(text[position..h.Start], false));
            }

            segments.Add(new TextSegment(text.Substring(h.Start, h.Length), true));
            position = h.Start + h.Length;
        }

        if (position < text.Length)
        {
            segments.Add(new TextSegment(text[position..], false));
        }

        return segments;
    }
}

public sealed class ResultItemViewModel(SearchResultItem item, TimeZoneInfo timeZone)
{
    public string Path { get; } = item.Path;

    public string FileName { get; } = System.IO.Path.GetFileName(item.Path);

    public string Directory { get; } = System.IO.Path.GetDirectoryName(item.Path) ?? "";

    public double Score { get; } = item.Score;

    public string ScoreText { get; } = item.Score.ToString("F3", CultureInfo.InvariantCulture);

    public string SizeText { get; } = Format.Size(item.Size);

    public string ModifiedText { get; } = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(item.LastWriteUtc, DateTimeKind.Utc), timeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

public sealed class SnippetViewModel(Snippet snippet)
{
    public int Start { get; } = snippet.Start;

    public IReadOnlyList<TextSegment> Segments { get; } = TextSegment.Split(snippet.Text, snippet.Highlights);
}

public sealed class PreviewViewModel(DocumentPreview preview)
{
    public string Path { get; } = preview.Path;

    public IReadOnlyList<TextSegment> NameSegments { get; } = TextSegment.Split(System.IO.Path.GetFileName(preview.Path), preview.NameHighlights);

    public IReadOnlyList<SnippetViewModel> Snippets { get; } = preview.Snippets.Select(s => new SnippetViewModel(s)).ToList();

    public string? Message { get; } = preview.Message;
}

public static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):F1} MB"),
    };
}
