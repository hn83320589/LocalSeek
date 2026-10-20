using LocalSeek.Core.Query;
using LocalSeek.Indexing.Preview;

namespace LocalSeek.App.ViewModels;

/// <summary>Runs work on the UI thread (the view model's thread).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Platform actions implemented by the app.</summary>
public interface IShellService
{
    void OpenFile(string path);

    void RevealInExplorer(string path);
}

public sealed record SearchResultItem(string Path, double Score, long Size, DateTime LastWriteUtc);

public abstract record SearchOutcome
{
    private SearchOutcome()
    {
    }

    public sealed record Success(BoundNode Query, IReadOnlyList<SearchResultItem> Hits, int TotalHits, TimeSpan Elapsed) : SearchOutcome;

    public sealed record Invalid(string Message, int Position, int Length) : SearchOutcome;
}

public interface ISearchService
{
    /// <summary>Parses and runs a query off the UI thread. Throws OperationCanceledException when cancelled.</summary>
    Task<SearchOutcome> SearchAsync(string query, CancellationToken cancellation);
}

public interface IPreviewService
{
    Task<DocumentPreview> GetPreviewAsync(string path, BoundNode query, CancellationToken cancellation);
}

public enum AppKey
{
    Up,
    Down,
    PageUp,
    PageDown,
    Home,
    End,
    Enter,
    Escape,
    K,
}

[Flags]
public enum AppKeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
}
